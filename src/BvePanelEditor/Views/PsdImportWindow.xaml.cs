using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using BvePanelEditor.Core.Psd;
using Microsoft.Win32;

namespace BvePanelEditor.Views;

public sealed class RoleOption
{
    public object? Value { get; init; }
    public string Label { get; init; } = "";
}

/// <summary>読み込み画面のレイヤー 1 行。</summary>
public sealed class PsdLayerNode : INotifyPropertyChanged
{
    public PsdLayer? Day;
    public PsdLayer? Night;
    public bool NightLoaded;
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public bool IsFolder { get; init; }
    public PsdLayerNode? ParentNode;
    public ObservableCollection<PsdLayerNode> Children { get; } = [];
    public Action? Changed;

    public static readonly RoleOption[] Options =
    [
        new() { Value = PsdRole.None, Label = "使わない" },
        new() { Value = PsdRole.Base, Label = "ベース" },
        new() { Value = PsdRole.Lamp, Label = "表示灯" },
        new() { Value = PsdRole.Number, Label = "数字のコマ" },
    ];
    public RoleOption[] RoleOptions => Options;

    private bool _expanded = true;
    public bool IsExpanded { get => _expanded; set { _expanded = value; Raise(nameof(IsExpanded)); } }

    private PsdRole _role;
    public PsdRole Role
    {
        get => _role;
        set
        {
            if (_role == value) return;
            _role = value;
            Raise(nameof(Role));
            Raise(nameof(RoleValue));
            Raise(nameof(NumberVisibility));
            for (var p = ParentNode; p != null; p = p.ParentNode) p.Raise(nameof(RoleValue));
            Changed?.Invoke();
        }
    }

    /// <summary>コンボボックス用。フォルダは中身の役割がそろっていればその役割、ばらばらなら空。</summary>
    public object? RoleValue
    {
        get
        {
            if (!IsFolder) return Role;
            var roles = Leaves().Where(l => l.Usable).Select(l => l.Role).Distinct().ToList();
            return roles.Count == 1 ? roles[0] : null;
        }
        set
        {
            if (value is not PsdRole r) return;
            if (!IsFolder) { Role = r; return; }
            foreach (var l in Leaves().Where(l => l.Usable))
            {
                if (r == PsdRole.Number && l.Role != PsdRole.Number) l.NumberSet = Name;
                l.Role = r;
            }
            Raise(nameof(RoleValue));
        }
    }

    private string _numberSet = "数字";
    public string NumberSet
    {
        get => _numberSet;
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? "数字" : value.Trim();
            if (_numberSet == v) return;
            _numberSet = v;
            Raise(nameof(NumberSet));
            Changed?.Invoke();
        }
    }

    public Visibility NumberVisibility => !IsFolder && Role == PsdRole.Number ? Visibility.Visible : Visibility.Collapsed;
    public PsdLayer? Layer => Day ?? Night;
    public bool Usable => IsFolder ? Children.Any(c => c.Usable) : (Day?.CanCompose ?? false) || (Night?.CanCompose ?? false);
    public string Eye => IsFolder || Layer == null ? (Layer?.Hidden == true ? "○" : "") : Layer.Hidden ? "○" : "◉";
    public string NightMark => IsFolder || !NightLoaded ? "" : Day != null && Night != null ? "昼夜" : Day != null ? "昼だけ" : "夜だけ";
    public string Notes => Layer is { } l && !IsFolder ? PsdImport.Notes(l) : "";
    public FontWeight NameWeight => IsFolder ? FontWeights.Bold : FontWeights.Normal;

    public IEnumerable<PsdLayerNode> Leaves()
    {
        if (!IsFolder) { yield return this; yield break; }
        foreach (var c in Children)
            foreach (var l in c.Leaves()) yield return l;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>デジタル数字のコマの並び 1 行。</summary>
public sealed class FrameRow
{
    public string Set { get; init; } = "";
    public string Path { get; init; } = "";
    public string Label { get; init; } = "";
    public override string ToString() => Label;
}

/// <summary>PSD から読み込む画面。レイヤーごとに役割を選ぶ。</summary>
public partial class PsdImportWindow : Window
{
    private readonly string _panelDir;
    private PsdFile? _day;
    private PsdFile? _night;
    private List<PsdLayerNode> _roots = [];
    private readonly Dictionary<string, List<string>> _frameOrder = [];
    private readonly DispatcherTimer _previewTimer;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _layer;

    /// <summary>読み込むと決まったときの指定（PSD はここで開いたものを渡すので、使い終わったら呼んだ側で閉じる）。</summary>
    public PsdImportSpec? Spec { get; private set; }
    public string? DayPath => _day?.FilePath ?? Spec?.Day.FilePath;
    public string? NightPath => _night?.FilePath ?? Spec?.Night?.FilePath;

    public PsdImportWindow(Window owner, string panelDir, string? dayPsd, string? nightPsd, int layer)
    {
        InitializeComponent();
        Owner = owner;
        _panelDir = panelDir;
        _layer = layer;
        OutBox.Text = System.IO.Path.Combine(panelDir, "psd");
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); _ = RefreshPreviewAsync(); };
        Closed += (_, _) =>
        {
            _previewTimer.Stop();
            _cts?.Cancel();
            if (Spec == null) { _day?.Dispose(); _night?.Dispose(); }
        };
        Loaded += (_, _) =>
        {
            if (dayPsd != null) LoadDay(dayPsd, nightPsd);
            else OnBrowseDay(this, new RoutedEventArgs());
        };
    }

    // ================= ファイル =================

    private void LoadDay(string path, string? nightPath = null)
    {
        try
        {
            var psd = PsdFile.Open(path);
            _day?.Dispose();
            _day = psd;
            DayBox.Text = path;
            Title = $"PSD から読み込み - {System.IO.Path.GetFileName(path)}（{psd.Width}×{psd.Height}）";
            var night = nightPath ?? GuessNight(path);
            if (night != null && File.Exists(night)) LoadNight(night, rebuild: false);
            BuildTree();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"PSD を読めませんでした:\n{e.Message}", "PSD から読み込み", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LoadNight(string path, bool rebuild = true)
    {
        try
        {
            var psd = PsdFile.Open(path);
            _night?.Dispose();
            _night = psd;
            NightBox.Text = path;
            if (_day != null && (psd.Width != _day.Width || psd.Height != _day.Height))
                MessageBox.Show(this, $"昼と夜の PSD の大きさが違います（昼 {_day.Width}×{_day.Height}、夜 {psd.Width}×{psd.Height}）。同じ座標として読み込みます。",
                    "PSD から読み込み", MessageBoxButton.OK, MessageBoxImage.Information);
            if (rebuild) BuildTree();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"夜の PSD を読めませんでした:\n{e.Message}", "PSD から読み込み", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>昼の PSD の名前から夜の PSD を探す（昼→夜、_d→_n、day→night）。</summary>
    public static string? GuessNight(string dayPath)
    {
        var dir = System.IO.Path.GetDirectoryName(dayPath) ?? "";
        var name = System.IO.Path.GetFileNameWithoutExtension(dayPath);
        var ext = System.IO.Path.GetExtension(dayPath);
        var candidates = new List<string>
        {
            name.Replace("昼", "夜"),
            Regex.Replace(name, "_d$", "_n"),
            Regex.Replace(name, "_D$", "_N"),
            Regex.Replace(name, "_d_", "_n_"),
            Regex.Replace(name, "day", "night", RegexOptions.IgnoreCase),
        };
        foreach (var c in candidates.Where(c => c != name).Distinct())
        {
            var p = System.IO.Path.Combine(dir, c + ext);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private void OnBrowseDay(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { AddToRecent = false, Title = "昼の PSD を選ぶ", Filter = "Photoshop|*.psd|すべて|*.*" };
        if (_day != null) dlg.InitialDirectory = System.IO.Path.GetDirectoryName(_day.FilePath);
        if (dlg.ShowDialog(this) == true) { NightBox.Text = ""; _night?.Dispose(); _night = null; LoadDay(dlg.FileName); }
        else if (_day == null) Close();
    }

    private void OnBrowseNight(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { AddToRecent = false, Title = "夜の PSD を選ぶ", Filter = "Photoshop|*.psd|すべて|*.*" };
        if (_day != null) dlg.InitialDirectory = System.IO.Path.GetDirectoryName(_day.FilePath);
        if (dlg.ShowDialog(this) == true) LoadNight(dlg.FileName);
    }

    private void OnClearNight(object sender, RoutedEventArgs e)
    {
        _night?.Dispose();
        _night = null;
        NightBox.Text = "";
        BuildTree();
    }

    private void OnBrowseOut(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "PNG を書き出すフォルダ" };
        if (Directory.Exists(OutBox.Text)) dlg.InitialDirectory = OutBox.Text;
        else dlg.InitialDirectory = _panelDir;
        if (dlg.ShowDialog(this) == true) OutBox.Text = dlg.FolderName;
    }

    // ================= レイヤーの一覧 =================

    private void BuildTree()
    {
        if (_day == null) return;
        // 作り直しても、同じ名前のレイヤーの役割は保つ
        var keep = new Dictionary<string, (PsdRole Role, string NumberSet)>();
        foreach (var l in _roots.SelectMany(r => r.Leaves())) keep.TryAdd(l.Path, (l.Role, l.NumberSet));
        var byPath = new Dictionary<string, PsdLayerNode>();
        var roots = new List<PsdLayerNode>();
        void Add(PsdLayerNode n, string? parentPath)
        {
            if (parentPath != null && byPath.TryGetValue(parentPath, out var parent))
            {
                n.ParentNode = parent;
                parent.Children.Add(n);
            }
            else roots.Add(n);
            byPath.TryAdd(n.Path, n);
        }

        for (int i = _day.Layers.Count - 1; i >= 0; i--) // 上（手前）から
        {
            var l = _day.Layers[i];
            if (l.Kind == PsdLayerKind.FolderEnd) continue;
            var n = new PsdLayerNode
            {
                Name = PsdImport.Leaf(l.Path), Path = l.Path, IsFolder = l.IsFolder, Day = l, // 同じ名前は「名前 #2」で見分ける
                Night = l.IsFolder ? null : _night?.Find(l.Path), NightLoaded = _night != null,
            };
            Add(n, l.Parent?.Path);
        }
        if (_night != null)
        {
            for (int i = _night.Layers.Count - 1; i >= 0; i--)
            {
                var l = _night.Layers[i];
                if (!l.CanCompose || _day.Find(l.Path) != null) continue;
                Add(new PsdLayerNode { Name = l.Name, Path = l.Path, Night = l, NightLoaded = true }, l.Parent?.Path);
            }
        }

        foreach (var leaf in roots.SelectMany(r => r.Leaves()))
        {
            if (keep.TryGetValue(leaf.Path, out var k)) { leaf.Role = k.Role; leaf.NumberSet = k.NumberSet; }
            else
            {
                leaf.Role = leaf.Day != null ? PsdImport.DefaultRole(leaf.Day, _day.Canvas) : PsdRole.None;
                leaf.NumberSet = leaf.ParentNode?.Name ?? "数字";
            }
            leaf.Changed = OnRolesChanged;
        }
        _roots = roots;
        LayerTree.ItemsSource = _roots;
        OnRolesChanged();
    }

    private IEnumerable<PsdLayerNode> Leaves => _roots.SelectMany(r => r.Leaves());

    private void OnDefaultRoles(object sender, RoutedEventArgs e)
    {
        foreach (var l in Leaves) l.Role = l.Day != null ? PsdImport.DefaultRole(l.Day, _day!.Canvas) : PsdRole.None;
    }

    private void OnRolesChanged()
    {
        RebuildNumbers();
        var leaves = Leaves.ToList();
        int bases = leaves.Count(l => l.Role == PsdRole.Base);
        int lamps = leaves.Count(l => l.Role == PsdRole.Lamp);
        // 再現できない効果があるレイヤーだけ数える
        int fx = leaves.Count(l => l.Role != PsdRole.None && l.Layer is { HasEffects: true } x && (x.Effects == null || x.Effects.Unsupported.Count > 0));
        SummaryText.Text = $"ベース {bases} 枚を重ねる・表示灯 {lamps} 個・数字 {_frameOrder.Count} 個"
            + (fx > 0 ? $"　※ 再現できないレイヤー効果のあるレイヤー {fx} 個（その効果なしで書き出します）" : "");
        _previewTimer.Stop();
        _previewTimer.Start();
        DrawOverlay();
    }

    // ================= デジタル数字 =================

    private static string NaturalKey(string s) => Regex.Replace(s, @"\d+", m => m.Value.PadLeft(8, '0'));

    private void RebuildNumbers()
    {
        var sets = Leaves.Where(l => l.Role == PsdRole.Number).GroupBy(l => l.NumberSet).ToList();
        foreach (var old in _frameOrder.Keys.Except(sets.Select(s => s.Key)).ToList()) _frameOrder.Remove(old);
        foreach (var g in sets)
        {
            var paths = g.Select(l => l.Path).ToList();
            var order = _frameOrder.TryGetValue(g.Key, out var o) ? o.Where(paths.Contains).ToList() : [];
            order.AddRange(paths.Where(p => !order.Contains(p)).OrderBy(p => NaturalKey(PsdImport.Leaf(p))));
            _frameOrder[g.Key] = order;
        }
        var selected = NumberList.SelectedItem as FrameRow;
        var rows = _frameOrder.SelectMany(kv => kv.Value.Select((p, i) => new FrameRow { Set = kv.Key, Path = p, Label = $"【{kv.Key}】 {i}: {PsdImport.Leaf(p)}" })).ToList();
        NumberList.ItemsSource = rows;
        if (selected != null) NumberList.SelectedItem = rows.FirstOrDefault(r => r.Set == selected.Set && r.Path == selected.Path);
    }

    private void MoveFrame(int d)
    {
        if (NumberList.SelectedItem is not FrameRow r || !_frameOrder.TryGetValue(r.Set, out var list)) return;
        int i = list.IndexOf(r.Path), j = i + d;
        if (i < 0 || j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
        RebuildNumbers();
    }

    private void OnFrameUp(object sender, RoutedEventArgs e) => MoveFrame(-1);
    private void OnFrameDown(object sender, RoutedEventArgs e) => MoveFrame(1);

    // ================= 見本 =================

    private async Task RefreshPreviewAsync()
    {
        if (_day == null) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var day = _day;
        var layers = Leaves.Where(l => l.Role == PsdRole.Base && l.Day != null).Select(l => l.Day!).ToList();
        int scale = Math.Max(1, (int)Math.Ceiling(Math.Max(day.Width, day.Height) / 2048.0));
        PreviewStatus.Text = "見本を作っています…";
        PsdPixels? img = null;
        // PSD は同時に 2 か所から読めないので、裏の処理は 1 つずつ。解放は裏のスレッドで行う（決定時に画面側で待てるように）
        await _gate.WaitAsync();
        if (cts.IsCancellationRequested) { _gate.Release(); return; }
        try
        {
            img = await Task.Run(() =>
            {
                try { return PsdCompose.Compose(day, layers, day.Canvas, scale, null, cts.Token); }
                finally { _gate.Release(); }
            });
        }
        catch (OperationCanceledException) { return; }
        if (cts.IsCancellationRequested || img == null) return;
        var bmp = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null, img.Bgra, img.Width * 4);
        bmp.Freeze();
        PreviewImage.Source = bmp;
        PreviewImage.Width = day.Width;
        PreviewImage.Height = day.Height;
        Overlay.Width = day.Width;
        Overlay.Height = day.Height;
        PreviewStatus.Text = $"ベース {layers.Count} 枚を重ねた見本（{(scale > 1 ? $"1/{scale} に縮小して表示" : "等倍")}）";
        DrawOverlay();
    }

    private static readonly Brush LampBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0x3C)));
    private static readonly Brush NumberBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x40, 0xE0, 0x70)));
    private static readonly Brush SelectBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x40)));
    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private PsdRect RectOf(PsdLayerNode n)
    {
        var r = default(PsdRect);
        foreach (var l in n.Leaves())
        {
            if (l.Day?.HasPixels == true) r = r.Union(l.Day.Bounds);
            if (l.Night?.HasPixels == true) r = r.Union(l.Night.Bounds);
        }
        return _day != null ? r.Intersect(_day.Canvas) : r;
    }

    private void DrawOverlay()
    {
        Overlay.Children.Clear();
        if (_day == null) return;
        double t = 1.5 / Math.Max(0.01, ZoomSlider.Value);
        void Box(PsdRect r, Brush b, double th, bool dashed)
        {
            if (r.IsEmpty) return;
            var rect = new Rectangle { Width = r.Width, Height = r.Height, Stroke = b, StrokeThickness = th };
            if (dashed) rect.StrokeDashArray = [4, 3];
            Canvas.SetLeft(rect, r.Left);
            Canvas.SetTop(rect, r.Top);
            Overlay.Children.Add(rect);
        }
        foreach (var l in Leaves.Where(l => l.Role == PsdRole.Lamp)) Box(RectOf(l), LampBrush, t, false);
        foreach (var (_, frames) in _frameOrder)
        {
            var r = default(PsdRect);
            foreach (var p in frames) if (Leaves.FirstOrDefault(l => l.Path == p) is { } n) r = r.Union(RectOf(n));
            Box(r, NumberBrush, t, true);
        }
        if (LayerTree.SelectedItem is PsdLayerNode sel) Box(RectOf(sel), SelectBrush, t * 2, false);
    }

    private void OnTreeSelection(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        DrawOverlay();
        if (e.NewValue is PsdLayerNode n && RectOf(n) is { IsEmpty: false } r)
        {
            // 選んだレイヤーが見えるところまでスクロールする
            double z = ZoomSlider.Value;
            PreviewScroll.ScrollToHorizontalOffset(Math.Max(0, (r.Left + r.Width / 2.0) * z - PreviewScroll.ViewportWidth / 2));
            PreviewScroll.ScrollToVerticalOffset(Math.Max(0, (r.Top + r.Height / 2.0) * z - PreviewScroll.ViewportHeight / 2));
        }
    }

    private void OnZoom(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PreviewScale == null) return;
        PreviewScale.ScaleX = PreviewScale.ScaleY = e.NewValue;
        DrawOverlay();
    }

    // ================= 決定 =================

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_day == null) return;
        var leaves = Leaves.ToList();
        var spec = new PsdImportSpec
        {
            Day = _day,
            Night = _night,
            PanelDir = _panelDir,
            OutputDir = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(OutBox.Text) ? OutBox.Text : System.IO.Path.Combine(_panelDir, OutBox.Text)),
            BaseName = PsdImport.SafeFileName(BaseNameBox.Text.Length > 0 ? BaseNameBox.Text : "base"),
            BaseLayers = leaves.Where(l => l.Role == PsdRole.Base).Select(l => l.Path).ToList(),
            LampLayers = leaves.Where(l => l.Role == PsdRole.Lamp).Select(l => l.Path).ToList(),
            Numbers = _frameOrder.Select(kv => new PsdNumberSpec { Name = kv.Key, Frames = kv.Value.ToList() }).ToList(),
            Layer = _layer,
        };
        if (spec.BaseLayers.Count + spec.LampLayers.Count + spec.Numbers.Count == 0)
        {
            MessageBox.Show(this, "役割を選んだレイヤーがありません。", "PSD から読み込み", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // 見本づくりを止めて、PSD を読み終わるのを待つ（このあと同じ PSD から書き出すため）
        _previewTimer.Stop();
        _cts?.Cancel();
        _gate.Wait();
        _gate.Release();
        Spec = spec;
        DialogResult = true;
    }

    /// <summary>確認用: 見本ができるまで待つ。</summary>
    public async Task WaitPreviewAsync()
    {
        _previewTimer.Stop();
        await RefreshPreviewAsync();
    }
}
