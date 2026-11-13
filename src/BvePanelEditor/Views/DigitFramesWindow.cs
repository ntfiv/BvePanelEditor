using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BvePanelEditor.Core;
using BvePanelEditor.Core.Psd;
using BvePanelEditor.Rendering;
using Microsoft.Win32;

namespace BvePanelEditor.Views;

/// <summary>デジタル数字の 1 コマ（昼と夜）。</summary>
public sealed class DigitFrame
{
    public string Label = "";
    /// <summary>昼の絵（null なら透明なコマ）。PSD のレイヤーから作ったコマは、同じ PSD のコマどうしで大きさ・位置がそろっている。</summary>
    public PsdPixels? Pixels;
    /// <summary>夜の絵（null なら夜の画像なし。コマ割では昼の絵を使う）。</summary>
    public PsdPixels? Night;
    public string? NightLabel;
    /// <summary>元の画像のファイル（PSD のレイヤーや透明なコマなら null）。</summary>
    public string? Source;
    public bool IsTransparent => DigitStrip.IsTransparent(Pixels);
}

/// <summary>
/// 単コマの画像からデジタル数字のコマ割を作る画面。画像を 1 枚ずつコマとして足し（PSD はレイヤーを 1 コマずつ。完全に透明なレイヤーも 1 コマ）、
/// 並べ替えて、上から順に縦に並べた画像にする。連番の画像（ID_motor_0.png → _1・_2…）は自動で読み込み、
/// 夜の画像は名前（_N・night・dark など）から自動で割り当てる（手でも選べる）。昼と夜は同じ大きさ・同じ並びにする。
/// </summary>
public sealed class DigitFramesWindow : Window
{
    /// <summary>確認用: 設定すると画面を出さずに、作った画面をこの関数に渡す（true を返せば「コマ割を作る」）。</summary>
    public static Func<DigitFramesWindow, bool>? TestAnswer;

    private readonly List<DigitFrame> _frames = [];
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Extended };
    private readonly DigitTestWindow.StripView _preview = new();
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Foreground = Brushes.DimGray };
    private readonly TextBox _name = new() { Width = 220, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly RadioButton _showDay = new() { Content = "昼", IsChecked = true, GroupName = "fdn", Margin = new Thickness(0, 0, 10, 0) };
    private readonly RadioButton _showNight = new() { Content = "夜", GroupName = "fdn" };
    private readonly List<string> _notes = [];

    public IReadOnlyList<DigitFrame> Frames => _frames;
    public string FrameName => _name.Text.Trim();
    /// <summary>夜の画像のあるコマがあるか。</summary>
    public bool HasNight => _frames.Any(f => f.Night != null);

    public DigitFramesWindow(Window owner, IEnumerable<string> files, string name)
    {
        Owner = owner;
        Title = "デジタル数字のコマ割を作る";
        Width = 960; Height = 740;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Yu Gothic UI");
        FontSize = 12;
        Background = SystemColors.WindowBrush;
        AllowDrop = true;
        _name.Text = name;
        Content = BuildUi();
        AddFiles(files, withSequence: true);
        Drop += (_, e) =>
        {
            // ドロップの処理中に画像・PSD を読んだりメッセージを出したりすると、ドラッグ元（エクスプローラーなど）が
            // そのあいだ固まるので、ドロップを終えてから読み込む
            if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
                Dispatcher.BeginInvoke(() => AddFiles(dropped, withSequence: true), System.Windows.Threading.DispatcherPriority.Background);
            e.Handled = true;
        };
    }

    /// <summary>画面を出す（確認用の答えがあれば出さない）。「コマ割を作る」なら true。</summary>
    public bool Ask() => TestAnswer != null ? TestAnswer(this) && Finish() : ShowDialog() == true;

    private UIElement BuildUi()
    {
        var root = new DockPanel();
        var top = new TextBlock
        {
            Text = "デジタル数字の 1 コマずつの画像から、コマ割の画像を作ります。上から 0 番、1 番…の順に並べます。"
                + "画像を足す（ここへドロップしても足せます。連番の画像は自動で読み込みます）・PSD はレイヤーを 1 コマずつ足します。"
                + "夜の画像は名前（_N・night・dark など）から自動で割り当て、「夜の画像を選ぶ…」でも指定できます。昼と夜は同じ大きさ・同じ並びにします。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        };
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var ok = new Button { Content = "コマ割を作る", Padding = new Thickness(14, 4, 14, 4), IsDefault = true, Margin = new Thickness(0, 0, 8, 0), FontWeight = FontWeights.SemiBold };
        ok.Click += (_, _) => { if (Finish()) DialogResult = true; };
        var cancel = new Button { Content = "キャンセル", Width = 90, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(new TextBlock { Text = "名前", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        nameRow.Children.Add(_name);
        nameRow.Children.Add(new TextBlock { Text = "（オブジェクトの名前と、書き出す画像のファイル名）", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
        bottom.Children.Add(nameRow);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new DockPanel { Margin = new Thickness(0, 0, 8, 0) };
        var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        void B(string text, Action act, string? tip = null)
        {
            var b = new Button { Content = text, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 4), ToolTip = tip };
            b.Click += (_, _) => act();
            tools.Children.Add(b);
        }
        B("画像を足す…", OnAddImages, "PNG などの画像は 1 枚 1 コマ（連番の画像も自動で読み込みます）、PSD はレイヤーを 1 コマずつ足します");
        B("透明なコマを足す", () => Insert([new DigitFrame { Label = "（透明なコマ）" }]), "何も表示しないコマ（数字を出さない桁など）");
        B("↑", () => MoveSelected(-1), "前（上）へ");
        B("↓", () => MoveSelected(1), "後ろ（下）へ");
        B("消す", RemoveSelected);
        B("夜の画像を選ぶ…", OnPickNight, "選んだコマの夜の画像を選びます（いくつか選んでいれば、選んだ画像を上から順に割り当てます）");
        B("夜の画像を外す", () => { foreach (var i in SelectedIndexes()) { _frames[i].Night = null; _frames[i].NightLabel = null; } Refresh(SelectedIndexes()); });
        DockPanel.SetDock(tools, Dock.Top);
        left.Children.Add(tools);
        left.Children.Add(_list);
        grid.Children.Add(left);

        var right = new DockPanel();
        Grid.SetColumn(right, 1);
        var dn = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        dn.Children.Add(new TextBlock { Text = "表示", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        dn.Children.Add(_showDay);
        dn.Children.Add(_showNight);
        _showDay.Checked += (_, _) => Refresh(SelectedIndexes());
        _showNight.Checked += (_, _) => Refresh(SelectedIndexes());
        DockPanel.SetDock(dn, Dock.Top);
        right.Children.Add(dn);
        DockPanel.SetDock(_info, Dock.Bottom);
        right.Children.Add(_info);
        right.Children.Add(new GroupBox
        {
            Header = "できあがり（上から 0 番）",
            Content = new ScrollViewer { Content = _preview, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.DimGray },
        });
        grid.Children.Add(right);
        root.Children.Add(grid);
        // 余白は Border の Padding で付ける（画面を画像に保存したときに端が切れないように）
        return new Border { Padding = new Thickness(10), Child = root };
    }

    private bool ShowingNight => _showNight.IsChecked == true;

    private void OnAddImages()
    {
        var dlg = new OpenFileDialog
        {
            Title = "コマにする画像（複数選べます）", Multiselect = true,
            Filter = "画像・PSD|*.png;*.bmp;*.jpg;*.jpeg;*.gif;*.tif;*.tiff;*.psd|すべてのファイル|*.*",
        };
        if (dlg.ShowDialog(this) == true) AddFiles(dlg.FileNames, withSequence: true);
    }

    /// <summary>
    /// 画像・PSD をコマとして足す（選んでいるコマのあと。なければ末尾）。PSD はレイヤーを 1 コマずつ（完全に透明なレイヤーも）。
    /// withSequence なら、連番の画像（ID_motor_0.png → _1・_2…）も足す。夜の画像は名前から探して割り当てる（夜の画像そのものはコマにしない）。
    /// </summary>
    public void AddFiles(IEnumerable<string> files, bool withSequence)
    {
        var given = files.Select(Path.GetFullPath).ToList();
        var list = new List<string>();
        foreach (var f in given.OrderBy(f => f, NaturalComparer.Instance))
        {
            bool psd = Path.GetExtension(f).Equals(".psd", StringComparison.OrdinalIgnoreCase);
            var seq = withSequence && !psd ? ImageNames.FindSequence(f) : [f];
            if (seq.Count > 1 && seq.Count(s => !given.Contains(s, StringComparer.OrdinalIgnoreCase)) > 0)
                _notes.Add($"連番の画像を {seq.Count} 枚読み込みました（{Path.GetFileName(seq[0])} 〜 {Path.GetFileName(seq[^1])}）");
            foreach (var s in seq) if (!list.Contains(s, StringComparer.OrdinalIgnoreCase)) list.Add(s);
        }
        // すでにコマにしている画像と、ほかの画像の夜の画像はコマにしない
        var nights = list.Select(ImageNames.FindNight).Where(n => n != null).Select(n => Path.GetFullPath(n!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var have = _frames.Where(f => f.Source != null).Select(f => f.Source!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        list = list.Where(f => !nights.Contains(f) && !have.Contains(f)).ToList();

        var added = new List<DigitFrame>();
        var errors = new List<string>();
        foreach (var f in list)
        {
            try
            {
                if (Path.GetExtension(f).Equals(".psd", StringComparison.OrdinalIgnoreCase)) added.AddRange(FramesFromPsd(f, errors));
                else
                {
                    var frame = new DigitFrame { Label = Path.GetFileName(f), Pixels = ImageLoader.Load(f).Pixels, Source = f };
                    if (ImageNames.FindNight(f) is { } nf && ImageLoader.TryLoadPixels(nf) is { } npx)
                    {
                        frame.Night = npx;
                        frame.NightLabel = Path.GetFileName(nf);
                    }
                    added.Add(frame);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
            {
                errors.Add($"「{Path.GetFileName(f)}」を読めませんでした（{ex.Message}）");
            }
        }
        if (added.Count > 0) Insert(added);
        if (errors.Count > 0)
        {
            _notes.AddRange(errors);
            if (TestAnswer == null && IsLoaded) MessageBox.Show(this, string.Join("\n", errors), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Refresh();
        }
    }

    /// <summary>
    /// PSD のレイヤーを 1 コマずつ（名前の末尾が数字なら数の順、そうでなければレイヤーパネルの上から順）。同じ PSD のコマは大きさ・位置をそろえる。
    /// 夜の PSD（名前に _N・night・dark など）があれば、同じ名前のレイヤーを夜のコマにする。
    /// </summary>
    private List<DigitFrame> FramesFromPsd(string path, List<string> errors)
    {
        using var psd = PsdFile.Open(path);
        var nightPath = ImageNames.FindNight(path);
        using var nightPsd = nightPath != null ? PsdFile.Open(nightPath) : null;
        var layers = DigitStrip.FrameOrder(DigitStrip.FrameLayers(psd));
        if (layers.Count == 0) { errors.Add($"「{Path.GetFileName(path)}」にコマにできるレイヤーがありません"); return []; }
        var warn = new List<string>();
        var num = PsdImport.RenderNumber(psd, nightPsd, layers.Select(l => l.Path).ToList(), warn);
        int fh = num != null ? num.Day.Height / layers.Count : 0;
        if (nightPsd != null) _notes.Add($"夜の PSD「{Path.GetFileName(nightPath)}」の同じ名前のレイヤーを夜のコマにしました");
        return layers.Select((l, i) => new DigitFrame
        {
            Label = $"{Path.GetFileName(path)} / {PsdFile.StripDuplicateMark(l.Path)}",
            Pixels = num != null ? DigitStrip.Frame(num.Day, fh, i) : null,
            Night = num?.Night != null ? DigitStrip.Frame(num.Night, fh, i) : null,
            NightLabel = num?.Night != null ? $"{Path.GetFileName(nightPath)} / {PsdFile.StripDuplicateMark(l.Path)}" : null,
        }).ToList();
    }

    /// <summary>選んだコマの夜の画像を選ぶ（いくつか選んでいれば、選んだ画像を名前の順に上から割り当てる）。</summary>
    private void OnPickNight()
    {
        var sel = SelectedIndexes();
        if (sel.Count == 0) { MessageBox.Show(this, "夜の画像を付けるコマを選んでください。", Title, MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var dlg = new OpenFileDialog { Title = "夜の画像", Multiselect = sel.Count > 1, Filter = "画像|*.png;*.bmp;*.jpg;*.jpeg;*.gif;*.tif;*.tiff|すべてのファイル|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        SetNight(sel, dlg.FileNames);
    }

    /// <summary>コマ（番号）に夜の画像を割り当てる（名前の順に上から）。</summary>
    public void SetNight(IReadOnlyList<int> frames, IEnumerable<string> files)
    {
        var list = files.OrderBy(f => f, NaturalComparer.Instance).ToList();
        for (int k = 0; k < frames.Count && k < list.Count; k++)
        {
            if (ImageLoader.TryLoadPixels(list[k]) is not { } px) { _notes.Add($"「{Path.GetFileName(list[k])}」を読めませんでした"); continue; }
            _frames[frames[k]].Night = px;
            _frames[frames[k]].NightLabel = Path.GetFileName(list[k]);
        }
        Refresh(frames);
    }

    private void Insert(List<DigitFrame> frames)
    {
        int at = _list.SelectedIndex >= 0 ? SelectedIndexes().Max() + 1 : _frames.Count;
        at = Math.Clamp(at, 0, _frames.Count);
        _frames.InsertRange(at, frames);
        Refresh(Enumerable.Range(at, frames.Count));
    }

    /// <summary>透明なコマを足す（確認用）。</summary>
    public void AddTransparentForTest(int at) { _frames.Insert(Math.Clamp(at, 0, _frames.Count), new DigitFrame { Label = "（透明なコマ）" }); Refresh(); }

    private List<int> SelectedIndexes() => _list.SelectedItems.Cast<ListBoxItem>().Select(i => _list.Items.IndexOf(i)).Where(i => i >= 0).Order().ToList();

    private void MoveSelected(int d)
    {
        var sel = SelectedIndexes();
        if (sel.Count == 0 || (d < 0 && sel[0] == 0) || (d > 0 && sel[^1] == _frames.Count - 1)) return;
        var moving = sel.Select(i => _frames[i]).ToList();
        foreach (var i in (d < 0 ? sel : Enumerable.Reverse(sel)))
            (_frames[i], _frames[i + d]) = (_frames[i + d], _frames[i]);
        Refresh(moving.Select(f => _frames.IndexOf(f)));
    }

    private void RemoveSelected()
    {
        var sel = SelectedIndexes();
        foreach (var i in Enumerable.Reverse(sel)) _frames.RemoveAt(i);
        Refresh(sel.Count > 0 && _frames.Count > 0 ? [Math.Min(sel[0], _frames.Count - 1)] : []);
    }

    /// <summary>昼・夜の画像のコマ割（同じ大きさ・同じ並び）。夜の画像のないコマは昼の絵を使う。夜の画像がひとつもなければ Night は null。</summary>
    public (PsdPixels Day, PsdPixels? Night, int Interval) Build()
    {
        var day = _frames.Select(f => f.Pixels).ToList();
        var night = _frames.Select(f => f.Night ?? f.Pixels).ToList();
        var (fw, fh) = DigitStrip.CellSize(HasNight ? day.Concat(night) : day);
        return (DigitStrip.Stack(day, fw, fh), HasNight ? DigitStrip.Stack(night, fw, fh) : null, fh);
    }

    /// <summary>一覧と、できあがりの見本を作り直す。</summary>
    private void Refresh(IEnumerable<int>? select = null)
    {
        var keep = select?.ToList();
        _list.Items.Clear();
        bool night = ShowingNight;
        for (int i = 0; i < _frames.Count; i++)
        {
            var f = _frames[i];
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            row.Children.Add(new TextBlock { Text = i.ToString(), Width = 28, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var shown = night ? f.Night ?? f.Pixels : f.Pixels;
            var thumb = new Border { Width = 48, Height = 32, Background = Brushes.DimGray, Margin = new Thickness(0, 0, 8, 0) };
            if (shown != null)
            {
                var img = new Image { Source = ImageLoader.ToBitmap(shown), Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
                thumb.Child = img;
            }
            row.Children.Add(thumb);
            var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            text.Inlines.Add(new System.Windows.Documents.Run(f.Label + (f.IsTransparent ? "（透明）" : f.Pixels != null ? $"　{f.Pixels.Width}×{f.Pixels.Height}" : ""))
            { Foreground = f.IsTransparent ? Brushes.Gray : Brushes.Black });
            if (!f.IsTransparent || f.Night != null)
                text.Inlines.Add(new System.Windows.Documents.Run(f.Night != null ? $"\n夜: {f.NightLabel}" : "\n夜: なし（昼の絵を使う）")
                { Foreground = f.Night != null ? new SolidColorBrush(Color.FromRgb(0x2A, 0x4F, 0x9C)) : Brushes.Gray, FontSize = 11 });
            row.Children.Add(text);
            _list.Items.Add(new ListBoxItem { Content = row });
        }
        if (keep != null) foreach (var i in keep) if (i >= 0 && i < _list.Items.Count) ((ListBoxItem)_list.Items[i]).IsSelected = true;

        if (_frames.Count == 0)
        {
            _info.Text = "コマがありません。「画像を足す…」か、画像をドロップしてください";
            _preview.Visibility = Visibility.Collapsed;
            return;
        }
        var (dayStrip, nightStrip, interval) = Build();
        var strip = night && nightStrip != null ? nightStrip : dayStrip;
        _preview.Visibility = Visibility.Visible;
        _preview.Set(ImageLoader.ToBitmap(strip), strip.Height, interval, -1);
        int pw = PngWriter.NextPow2(strip.Width), ph = PngWriter.NextPow2(strip.Height);
        var sizes = _frames.SelectMany(f => new[] { f.Pixels, f.Night }).Where(p => p != null).Select(p => (p!.Width, p.Height)).Distinct().ToList();
        int nightCount = _frames.Count(f => f.Night != null);
        _info.Text = $"{_frames.Count} コマ・1 コマ {strip.Width}×{interval}（いちばん大きいコマに合わせ、左上に詰めます）。"
            + $"できあがりの画像は {strip.Width}×{strip.Height}" + (pw != strip.Width || ph != strip.Height ? $"。BVE のパネル用に {pw}×{ph} に広げます（透明で埋めます）" : "")
            + $"\n夜の画像: {(nightCount == 0 ? "なし" : nightCount == _frames.Count ? "全部のコマにあり" : $"{nightCount} コマ（ないコマは昼の絵を使います）")}"
            + (night && nightStrip == null ? "（夜の画像がないので、昼の絵を出しています）" : "")
            + (sizes.Count > 1 ? "\n※ コマの大きさがそろっていません（" + string.Join("・", sizes.Take(4).Select(s => $"{s.Width}×{s.Height}")) + "）" : "")
            + (!_frames.Any(f => f.IsTransparent) ? "\n透明なコマ（何も表示しないコマ）はまだありません" : "")
            + (_notes.Count > 0 ? "\n" + string.Join("\n", _notes.TakeLast(3)) : "");
    }

    /// <summary>
    /// 「コマ割を作る」: コマがなければ止める。透明なコマがなければ、用意するか聞く（先頭に足す・末尾に足す・足さない・やめる）。
    /// </summary>
    private bool Finish()
    {
        if (_frames.Count == 0) { if (TestAnswer == null) MessageBox.Show(this, "コマがありません。", Title, MessageBoxButton.OK, MessageBoxImage.Information); return false; }
        if (FrameName.Length == 0) _name.Text = "digit";
        if (_frames.Any(f => f.IsTransparent)) return true;
        int pick = ChoiceDialog.Show(this, "透明なコマ",
            "透明なコマ（何も表示しないコマ）がありません。用意しますか？\n（例: 値が 0 のとき桁を消したいときは、0 番に透明なコマを置きます）",
            ["先頭（0 番）に透明なコマを足す", "末尾に透明なコマを足す", "足さない"]);
        if (pick < 0) return false;
        if (pick == 0) _frames.Insert(0, new DigitFrame { Label = "（透明なコマ）" });
        else if (pick == 1) _frames.Add(new DigitFrame { Label = "（透明なコマ）" });
        Refresh();
        return true;
    }

    public List<string> LabelsForTest => _frames.Select(f => f.Label).ToList();
    public List<string?> NightLabelsForTest => _frames.Select(f => f.NightLabel).ToList();
    public string InfoForTest => _info.Text;
    public void ShowNightForTest(bool night) { if (night) _showNight.IsChecked = true; else _showDay.IsChecked = true; }
}

/// <summary>名前の中の数を数として比べる（1, 2, 10 の順）。</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? a, string? b)
    {
        a ??= ""; b ??= "";
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a[si..i].TrimStart('0'); var nb = b[sj..j].TrimStart('0');
                int c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                int c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
