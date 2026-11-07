using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BvePanelEditor.Core;
using BvePanelEditor.Core.Psd;
using BvePanelEditor.Rendering;
using Microsoft.Win32;

namespace BvePanelEditor.Views;

/// <summary>
/// 指針を置く画面。標準の針（同梱の free_needle）か、ほかの針の画像を選び、回転の中心・角度・値の範囲を決める。
/// </summary>
public sealed class NeedlePickerWindow : Window
{
    private readonly ListBox _list = new() { Width = 250 };
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _pivotNote = new() { Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _presetNote = new() { Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    private readonly StackPanel _form = new();
    private readonly Dictionary<string, TextBox> _boxes = [];
    private readonly ComboBox _subject = new() { IsEditable = true, Width = 160 };
    private readonly ComboBox _which = new() { Width = 160 }; // 時計など何本かあるとき、どの針の設定か
    private List<NeedleSpec> _needles = [];
    private List<NeedlePreset> _presets = [];
    private readonly TextBlock _listWarnings = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4), Visibility = Visibility.Collapsed };
    private DateTime _configTime;
    private NeedleSpec? _cur;
    private PsdPixels? _curPx;
    private bool _suppress;

    /// <summary>［置く］で決まった針（時計なら 3 本）。</summary>
    public List<NeedleSpec>? Result { get; private set; }

    /// <summary>標準の針の組（圧力計Ａ・時計など 2 本以上）のときの名前。置いた針をこの名前でグループにする。</summary>
    public string? ResultGroupName { get; private set; }
    private string? _presetName;

    private sealed record Item(string Label, NeedlePreset? Preset)
    {
        public override string ToString() => Label;
    }

    /// <param name="initialImage">最初に使う針の画像（ドロップした画像など）。null なら標準の針の一覧の最初。</param>
    public NeedlePickerWindow(Window owner, string? initialImage = null)
    {
        Owner = owner;
        Title = "指針を置く";
        Width = 1020; Height = 740;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Yu Gothic UI");
        FontSize = 12;
        Background = SystemColors.WindowBrush;

        var root = new DockPanel { Margin = new Thickness(10) };

        // 左: 針の一覧
        var left = new DockPanel { Margin = new Thickness(0, 0, 10, 0) };
        var head = new TextBlock { Text = "針の一覧（標準の針はパネルくんに同梱。自由に使えます。並べ替えは ↑↓、追加・削除・値の変更は設定ファイルで）", Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap, Width = 250 };
        DockPanel.SetDock(head, Dock.Top);
        left.Children.Add(head);
        var tools = new StackPanel { Margin = new Thickness(0, 6, 0, 0), Width = 250 };
        Button Small(string text, string tip, Action click)
        {
            var b = new Button { Content = text, ToolTip = tip, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 4, 4) };
            b.Click += (_, _) => click();
            return b;
        }
        var row1 = new WrapPanel();
        row1.Children.Add(Small("↑", "一覧で上へ（設定ファイルを書き直します）", () => MoveSelected(-1)));
        row1.Children.Add(Small("↓", "一覧で下へ（設定ファイルを書き直します）", () => MoveSelected(1)));
        row1.Children.Add(Small("設定ファイルを編集", "一覧の設定ファイル（exe と同じフォルダの needles.txt）をメモ帳で開きます。保存してこの画面に戻ると読み直します", EditConfig));
        var row2 = new WrapPanel();
        row2.Children.Add(Small("ほかの針の画像を選ぶ…", "一覧にない針の画像を使います", PickOther));
        tools.Children.Add(_listWarnings);
        tools.Children.Add(row1);
        tools.Children.Add(row2);
        DockPanel.SetDock(tools, Dock.Bottom);
        left.Children.Add(tools);
        LoadPresets();
        RefreshList(-1);
        // メモ帳で設定ファイルを書き換えて戻ってきたら、読み直す
        Activated += (_, _) => { if (ConfigTime() != _configTime) ReloadConfig(); };
        _list.SelectionChanged += (_, _) =>
        {
            if (_suppress || _list.SelectedItem is not ListBoxItem { Tag: NeedlePreset p }) return;
            Use(p.Needles.Select(n => n.Clone()).ToList(), p.Note);
            _presetName = GroupNameOf(p.Label);
        };
        left.Children.Add(_list);
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(left);

        // 下: ボタン
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Button { Content = "パネルに置く", Width = 110, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => OnOk();
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "キャンセル", Width = 90, IsCancel = true });
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        // 右: 設定
        var right = new StackPanel { Width = 300, Margin = new Thickness(10, 0, 0, 0) };
        right.Children.Add(_presetNote);
        var whichRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        whichRow.Children.Add(new TextBlock { Text = "設定する針", Width = 110, VerticalAlignment = VerticalAlignment.Center });
        whichRow.Children.Add(_which);
        _which.SelectionChanged += (_, _) => { if (!_suppress && _which.SelectedIndex >= 0) Show(NeedleAt(_which.SelectedIndex)); };
        right.Children.Add(whichRow);
        right.Children.Add(_form);
        BuildForm();
        right.Children.Add(_pivotNote);
        right.Children.Add(new TextBlock
        {
            Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
            Text = "針はパネル画面の真ん中に置きます。置いた針は選んでいる間だけ位置ロック中でも動かせるので、ドラッグで文字盤の中心へ動かし、黄色い四角のドラッグか ＋／－ キー（Shift で 5px）で半径（針の長さ）を合わせてください。組で置いた針（圧力計Ａ・時計など）はグループにし、大きさの比をそのままにまとめて変わります。画像はパネルの needle フォルダにコピーします。",
        });
        DockPanel.SetDock(right, Dock.Right);
        root.Children.Add(right);

        // 中央: 見本（回転の中心に十字）
        var hint = new TextBlock { Text = "見本（赤い十字が回転の中心。画像をクリックすると、そこを中心にします）", Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap };
        var center = new DockPanel();
        DockPanel.SetDock(hint, Dock.Top);
        center.Children.Add(hint);
        var border = new Border { Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xE4, 0xDC)), Child = _preview, Padding = new Thickness(8) };
        center.Children.Add(border);
        _preview.MouseLeftButtonDown += OnPreviewClick;
        _preview.Cursor = Cursors.Cross;
        root.Children.Add(center);
        Content = root;

        Loaded += (_, _) =>
        {
            if (initialImage != null) UseImage(initialImage);
            else if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        };
    }

    private static ListBoxItem MakeRow(NeedlePreset p)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2) };
        var thumbs = new StackPanel { Orientation = Orientation.Horizontal, Width = 60, Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xE4, 0xDC)) };
        foreach (var n in p.Needles.Take(3))
            thumbs.Children.Add(new Image { Source = Load(n.DayFile), Height = 56, Margin = new Thickness(1), Stretch = Stretch.Uniform });
        row.Children.Add(thumbs);
        bool missing = p.Needles.Any(n => !File.Exists(n.DayFile));
        row.Children.Add(new TextBlock { Text = p.Label + (missing ? "（画像が見つかりません）" : ""), Foreground = missing ? Brushes.Firebrick : Brushes.Black, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), TextWrapping = TextWrapping.Wrap, Width = 160 });
        return new ListBoxItem { Content = row, Tag = p };
    }

    private static BitmapSource? Load(string path)
    {
        try { return ImageLoader.Load(path).Bitmap; }
        catch (Exception e) when (e is IOException or NotSupportedException or ArgumentException) { return null; }
    }

    private static readonly (string Key, string Label, string Tip)[] Fields =
    [
        ("Minimum", "最小の値", "針がいちばん左（開始の角度）を指す値"),
        ("Maximum", "最大の値", "針がいちばん右（終了の角度）を指す値"),
        ("InitialAngle", "開始の角度", "最小の値のときの角度（度。真上が 0、右回りが正）"),
        ("LastAngle", "終了の角度", "最大の値のときの角度（度）"),
        ("Radius", "半径", "針の長さ（px）。画像の回転の中心から上の端までがこの長さになるよう縮めます"),
        ("OriginX", "回転の中心 X", "針の画像の中の、回る軸の位置（px）"),
        ("OriginY", "回転の中心 Y", ""),
        ("NaturalFreq", "固有角振動数", "針の動きのばねの強さ（rad/s）。大きいほど速く追いつきます。0 なら値にすぐ追いつきます（キーを書きません）"),
        ("DampingRatio", "減衰比", "針の揺れの収まり方。1 で揺れずに止まり、小さいほど行き過ぎて揺れます。固有角振動数が 0 なら使いません"),
    ];

    private void BuildForm()
    {
        var subj = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        subj.Children.Add(new TextBlock { Text = "状態量", Width = 110, VerticalAlignment = VerticalAlignment.Center });
        _subject.ItemsSource = Subjects.Suggestions().ToList();
        _subject.ToolTip = "針が指す値（kmph・bc・mr・hour・min・sec など）";
        subj.Children.Add(_subject);
        _form.Children.Add(subj);
        foreach (var (key, label, tip) in Fields)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2), ToolTip = tip };
            row.Children.Add(new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center });
            var box = new TextBox { Width = 90 };
            box.LostKeyboardFocus += (_, _) => Commit();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
            _boxes[key] = box;
            row.Children.Add(box);
            _form.Children.Add(row);
        }
        _subject.LostKeyboardFocus += (_, _) => Commit();
    }

    private void Use(List<NeedleSpec> needles, string note)
    {
        _needles = needles;
        _presetNote.Text = note;
        _suppress = true;
        _suppress = false;
        RefreshWhich(0);
        _pivotNote.Text = "";
    }

    private void Show(NeedleSpec n)
    {
        _cur = n;
        _curPx = ImageLoader.TryLoadPixels(n.DayFile);
        _suppress = true;
        _subject.Text = n.Subject;
        string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        _boxes["Minimum"].Text = F(n.Minimum); _boxes["Maximum"].Text = F(n.Maximum);
        _boxes["InitialAngle"].Text = F(n.InitialAngle); _boxes["LastAngle"].Text = F(n.LastAngle);
        _boxes["Radius"].Text = F(n.Radius);
        _boxes["OriginX"].Text = n.OriginX.ToString(); _boxes["OriginY"].Text = n.OriginY.ToString();
        _boxes["NaturalFreq"].Text = F(n.NaturalFreq); _boxes["DampingRatio"].Text = F(n.DampingRatio);
        _suppress = false;
        RenderPreview();
    }

    private void Commit()
    {
        if (_suppress || _cur == null) return;
        double D(string k, double old) => double.TryParse(_boxes[k].Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : old;
        _cur.Subject = _subject.Text.Trim().Length > 0 ? _subject.Text.Trim() : _cur.Subject;
        _cur.Minimum = D("Minimum", _cur.Minimum); _cur.Maximum = D("Maximum", _cur.Maximum);
        _cur.InitialAngle = D("InitialAngle", _cur.InitialAngle); _cur.LastAngle = D("LastAngle", _cur.LastAngle);
        _cur.Radius = Math.Max(1, D("Radius", _cur.Radius));
        _cur.OriginX = (int)Math.Round(D("OriginX", _cur.OriginX)); _cur.OriginY = (int)Math.Round(D("OriginY", _cur.OriginY));
        _cur.NaturalFreq = Math.Max(0, D("NaturalFreq", _cur.NaturalFreq)); _cur.DampingRatio = Math.Max(0, D("DampingRatio", _cur.DampingRatio));
        // 時計のように同じ位置に置く針は、回転の中心をそろえることが多いので、ほかの針は変えない（必要なら 1 本ずつ直す）
        RenderPreview();
    }

    /// <summary>見本: 針の画像を拡大し、回転の中心に赤い十字を描く。</summary>
    private void RenderPreview()
    {
        if (_cur == null || _curPx == null) { _preview.Source = null; return; }
        var px = _curPx;
        double s = Math.Max(1, Math.Floor(400.0 / Math.Max(px.Width, px.Height) * 1.0));
        int w = (int)(px.Width * s), h = (int)(px.Height * s);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE8, 0xE4, 0xDC)), null, new Rect(0, 0, w, h));
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.NearestNeighbor);
            dc.DrawImage(ImageLoader.ToBitmap(px), new Rect(0, 0, w, h));
            var c = new Point((_cur.OriginX + 0.5) * s, (_cur.OriginY + 0.5) * s);
            var pen = new Pen(Brushes.Red, 1.5);
            dc.DrawLine(pen, c - new Vector(12, 0), c + new Vector(12, 0));
            dc.DrawLine(pen, c - new Vector(0, 12), c + new Vector(0, 12));
            dc.DrawEllipse(null, pen, c, 5, 5);
        }
        var bmp = new RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        _preview.Source = bmp;
        _previewScale = s;
    }

    private double _previewScale = 1;

    private void OnPreviewClick(object sender, MouseButtonEventArgs e)
    {
        if (_cur == null || _preview.Source is not BitmapSource src) return;
        var p = e.GetPosition(_preview);
        // Image は Uniform で縮むので、表示の大きさから元の画像の座標へ
        double k = src.PixelWidth / Math.Max(1, _preview.ActualWidth);
        _cur.OriginX = (int)Math.Floor(p.X * k / _previewScale);
        _cur.OriginY = (int)Math.Floor(p.Y * k / _previewScale);
        _boxes["OriginX"].Text = _cur.OriginX.ToString();
        _boxes["OriginY"].Text = _cur.OriginY.ToString();
        RenderPreview();
    }

    /// <summary>ほかの針の画像: 回転の中心は画像から探す（軸の丸）。夜の画像は名前から探す。</summary>
    private void PickOther()
    {
        var dlg = new OpenFileDialog { AddToRecent = false, Title = "針の画像を選ぶ（上向きに描いたもの）", Filter = "画像|*.png;*.bmp;*.jpg;*.jpeg;*.gif|すべて|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        UseImage(dlg.FileName);
    }

    /// <summary>ほかの針の画像を使う（回転の中心は画像から探す）。</summary>
    private void UseImage(string file)
    {
        var n = SpecFromImage(file);
        if (n == null) return;
        var pivot = _lastPivot;
        _suppress = true;
        _list.SelectedIndex = -1;
        _suppress = false;
        Use([n], n.NightFile != null ? $"夜の画像: {Path.GetFileName(n.NightFile)}" : "夜の画像は見つかりませんでした（昼の画像を使います）");
        // 見つからなかったときだけ知らせる
        _pivotNote.Text = pivot is { Found: true } ? "" : pivot?.Reason ?? "回転の中心を見つけられませんでした。画像をクリックして決めてください。";
        _presetName = null;
    }

    private NeedlePivotResult? _lastPivot;

    /// <summary>針の画像から設定を作る（回転の中心は画像から探す。夜の画像は名前から探す）。読めなければ null。</summary>
    private NeedleSpec? SpecFromImage(string file)
    {
        var px = ImageLoader.TryLoadPixels(file);
        if (px == null) { MessageBox.Show(this, "画像を読めませんでした。", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return null; }
        var pivot = _lastPivot = NeedlePivot.Find(px);
        return new NeedleSpec
        {
            Title = Path.GetFileNameWithoutExtension(file), DayFile = file, NightFile = FindNight(file),
            OriginX = pivot?.X ?? px.Width / 2, OriginY = pivot?.Y ?? px.Height - 1,
            Radius = 60, InitialAngle = -135, LastAngle = 135, Minimum = 0, Maximum = 160, Subject = "kmph",
        };
    }

    /// <summary>夜の画像を名前から探す（_dark・_N・N・_night・night・_夜 など。連番なら数の前に付けたものも）。</summary>
    public static string? FindNight(string day) => Core.ImageNames.FindNight(day);

    private void OnOk()
    {
        Commit();
        if (_needles.Count == 0) return;
        Result = _needles;
        ResultGroupName = _needles.Count > 1 ? _presetName : null;
        DialogResult = true;
    }

    /// <summary>「設定する針」の k 番目（上に重なる針から順）。</summary>
    private NeedleSpec NeedleAt(int k) => _needles[_needles.Count - 1 - k];

    /// <summary>「設定する針」に出す名前（上に重なる針から順）。</summary>
    public List<string> WhichForTest => _which.Items.Cast<string>().ToList();

    private void RefreshWhich(int select)
    {
        _suppress = true;
        // 上に重なる針から順に出す（組は下から順に持っているので逆にする。影の針が先に来ないように）
        _which.ItemsSource = Enumerable.Range(0, _needles.Count).Select(k => NeedleAt(k)).Select(n => n.Title.Length > 0 ? n.Title : "（名前なし）").ToList();
        _which.SelectedIndex = Math.Clamp(select, 0, Math.Max(0, _needles.Count - 1));
        _which.IsEnabled = _needles.Count > 1;
        _suppress = false;
        if (_needles.Count > 0) Show(NeedleAt(_which.SelectedIndex));
    }

    // ---- 一覧の編集（変えたらすぐ保存する） ----

    private void RefreshList(int select)
    {
        _suppress = true;
        _list.Items.Clear();
        foreach (var p in _presets) _list.Items.Add(MakeRow(p));
        _list.SelectedIndex = Math.Clamp(select, -1, _list.Items.Count - 1);
        _suppress = false;
    }

    private static DateTime ConfigTime() => File.Exists(NeedleCatalog.ConfigFile) ? File.GetLastWriteTimeUtc(NeedleCatalog.ConfigFile) : default;

    private void LoadPresets()
    {
        _presets = NeedleCatalog.Presets();
        _configTime = ConfigTime();
        var w = NeedleCatalog.LastWarnings;
        _listWarnings.Text = w.Count == 0 ? "" : "設定ファイルの問題: " + string.Join(" ／ ", w.Take(5)) + (w.Count > 5 ? $" ほか {w.Count - 5} 件" : "");
        _listWarnings.Visibility = w.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>設定ファイルを読み直す（選んでいた行は名前で選び直す）。</summary>
    public void ReloadConfig()
    {
        var label = SelectedRow >= 0 && SelectedRow < _presets.Count ? _presets[SelectedRow].Label : null;
        LoadPresets();
        RefreshList(-1);
        int i = label != null ? _presets.FindIndex(p => p.Label == label) : -1;
        _list.SelectedIndex = i >= 0 ? i : _presets.Count > 0 ? 0 : -1;
    }

    private void Persist(int select)
    {
        try
        {
            NeedleCatalog.Save(_presets);
            _configTime = ConfigTime();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"針の一覧の設定ファイルを書けませんでした:\n{e.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshList(select);
    }

    private int SelectedRow => _list.SelectedIndex;

    public void MoveSelected(int delta)
    {
        int i = SelectedRow, j = i + delta;
        if (i < 0 || j < 0 || j >= _presets.Count) return;
        (_presets[i], _presets[j]) = (_presets[j], _presets[i]);
        Persist(j);
    }

    /// <summary>設定ファイルをメモ帳で開く（なければ今の一覧で作る）。</summary>
    private void EditConfig()
    {
        try
        {
            if (!File.Exists(NeedleCatalog.ConfigFile)) { NeedleCatalog.Save(_presets); _configTime = ConfigTime(); }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{NeedleCatalog.ConfigFile}\"") { UseShellExecute = false });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, $"設定ファイルを開けませんでした:\n{NeedleCatalog.ConfigFile}\n{e.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>組の名前（「時計（時・分・秒の 3 本）」→「時計」）。</summary>
    private static string GroupNameOf(string label)
    {
        int k = label.IndexOf('（');
        return (k > 0 ? label[..k] : label).Trim();
    }

    // ---- 確認用 ----
    public string? PresetNameForTest => _presetName;
    public string FieldForTest(string key) => _boxes[key].Text;
    public void SetFieldForTest(string key, string text) { _boxes[key].Text = text; Commit(); }
    public string PivotNoteForTest => _pivotNote.Text;

    public void SelectPresetForTest(string labelStart)
    {
        _list.SelectedItem = null;
        _list.SelectedItem = _list.Items.OfType<ListBoxItem>().First(i => i.Tag is NeedlePreset p && p.Label.StartsWith(labelStart));
    }
    public List<string> LabelsForTest => _presets.Select(p => p.Label).ToList();
    public int SelectedRowForTest => SelectedRow;
    public void UseImageForTest(string file) => UseImage(file);
    public string ListWarningsForTest => _listWarnings.Text;
    public List<NeedleSpec> CurrentForTest => _needles;
}
