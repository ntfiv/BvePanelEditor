using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BvePanelEditor.Core;
using BvePanelEditor.Core.Crop;
using BvePanelEditor.Core.Psd;
using BvePanelEditor.Rendering;

namespace BvePanelEditor.Views;

/// <summary>ふたを切り抜く元（パネル本体の画像・表示灯の画像・PSD のレイヤー）。</summary>
public sealed class CoverSource
{
    public required string Label;
    /// <summary>画像（または PSD）のフルパス。</summary>
    public required string DayPath;
    public string? NightPath;
    /// <summary>画像の左上が、パネルのどこにあるか。</summary>
    public int X, Y;
    /// <summary>この画像を切り抜いた元の組（あればそれとつなぐ）。</summary>
    public string? LinkSet;
    /// <summary>PSD のレイヤーを重ねたものなら、そのレイヤー。</summary>
    public List<string>? DayLayers, NightLayers;
    public PsdPixels? Day, Night;
    public bool IsPsd => DayLayers != null;

    /// <summary>画像を読む（読めなければ false）。</summary>
    public bool EnsureLoaded()
    {
        Day ??= ImageLoader.TryLoadPixels(DayPath);
        if (Night == null && NightPath != null) Night = ImageLoader.TryLoadPixels(NightPath);
        return Day != null;
    }

    /// <summary>パネルの座標の四角を切り抜き、形の外を透明にする（shapes はパネルの座標）。</summary>
    public PsdPixels? Cut(bool night, IReadOnlyList<CropShape> shapes)
    {
        var src = night ? Night : Day;
        var rect = CropShape.Bounds(shapes);
        if (src == null || rect.IsEmpty) return null;
        var cut = RegionDetect.CropOf(src, new PsdRect(rect.Left - X, rect.Top - Y, rect.Right - X, rect.Bottom - Y));
        return CropShape.Mask(cut, shapes.Select(s => s.Offset(-rect.Left, -rect.Top)).ToList());
    }
}

/// <summary>ふたを作る画面の結果。形はパネルの座標。</summary>
public sealed record CoverResult(CoverSource Source, List<CropShape> Shapes, string Name);

/// <summary>
/// 指針のふたを作る画面。パネル本体の画像（か PSD のレイヤー）から、針の軸のふたを丸や多角形で切り抜く。
/// 丸は開いたときに軸のまわりの円を探して自動で置く。
/// </summary>
public sealed class CoverWindow : Window
{
    private readonly List<CoverSource> _sources;
    private readonly List<RenderItem> _needles;
    private readonly Func<CoverSource?>? _pickPsd;
    private readonly CoverCanvas _canvas = new();
    private readonly ComboBox _sourceCombo = new() { Margin = new Thickness(0, 2, 0, 6) };
    private readonly RadioButton _day = new() { Content = "昼", IsChecked = true, Margin = new Thickness(0, 0, 12, 0) };
    private readonly RadioButton _night = new() { Content = "夜" };
    private readonly CheckBox _showNeedle = new() { Content = "針を重ねて、ふたで隠れるか見る", IsChecked = true, Margin = new Thickness(0, 6, 0, 2) };
    private readonly Slider _value = new() { Minimum = 0, Maximum = 1, Value = 0, Margin = new Thickness(0, 2, 0, 6), ToolTip = "針の値（最小〜最大）" };
    private readonly Button _auto = new() { Content = "切り抜きの自動検出", ToolTip = "針の軸のまわりからふたの丸を探して置きます。もう一度押すと、ほかに見つかった丸（文字盤の輪など）に順に切り替えます", Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 2, 0, 2) };
    private readonly Button _polygon = new() { Content = "多角形を描く", Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 2, 0, 2) };
    private readonly Button _circle = new() { Content = "丸を足す", Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 2, 0, 2) };
    private readonly Button _ellipse = new() { Content = "楕円を足す", Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 2, 0, 2) };
    private readonly Button _delete = new() { Content = "選んだ形を消す", Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 2, 0, 2) };
    private readonly TextBox _name = new() { Margin = new Thickness(0, 2, 0, 6) };
    private readonly Image _dayPreview = new() { Stretch = Stretch.Uniform, Height = 110 };
    private readonly Image _nightPreview = new() { Stretch = Stretch.Uniform, Height = 110 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 6, 0, 0) };
    private List<CoverCircleResult> _candidates = [];
    private int _candidate = -1;
    private int _shownSource = -1;
    private bool _suppress;

    public CoverResult? Result { get; private set; }

    /// <param name="sources">切り抜く元の候補（上から順に、針のすぐ下にあるもの）。</param>
    /// <param name="needles">ふたをかぶせる針（同じ位置の針はまとめて）。</param>
    /// <param name="pickPsd">「PSD のレイヤーから選ぶ」を選んだとき、PSD とレイヤーを選ばせる（やめたら null）。</param>
    public CoverWindow(Window owner, List<CoverSource> sources, List<RenderItem> needles, string name, Func<CoverSource?>? pickPsd)
    {
        Owner = owner;
        Title = "指針のふたを作る";
        Width = 1100; Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Yu Gothic UI");
        FontSize = 12;
        Background = SystemColors.WindowBrush;
        _sources = sources;
        _needles = needles;
        _pickPsd = pickPsd;
        _name.Text = name;
        _canvas.Needles = needles;
        _canvas.ShowNeedle = true;
        Content = BuildUi();

        _canvas.ShapesChanged += UpdatePreview;
        _canvas.SelectionChanged += UpdateButtons;
        _canvas.StatusChanged += t => _status.Text = t;
        _day.Checked += (_, _) => { ShowSide(); UpdatePreview(); };
        _night.Checked += (_, _) => { ShowSide(); UpdatePreview(); };
        _showNeedle.Click += (_, _) => { _canvas.ShowNeedle = _showNeedle.IsChecked == true; _canvas.InvalidateVisual(); };
        _value.ValueChanged += (_, _) => { _canvas.NeedleValue = _value.Value; _canvas.InvalidateVisual(); };
        _auto.Click += (_, _) => NextCandidate();
        _circle.Click += (_, _) => AddCircle(false);
        _ellipse.Click += (_, _) => AddCircle(true);
        _polygon.Click += (_, _) => { _canvas.BeginPolygon(); _canvas.Focus(); };
        _delete.Click += (_, _) => { _canvas.DeleteSelected(); _canvas.Focus(); };
        _sourceCombo.SelectionChanged += (_, _) => OnSourceChanged();
        Loaded += (_, _) =>
        {
            RefreshSources(0);
            if (_needles.Count > 0) _canvas.CenterOn(_needles[0].Location, Math.Max(40, _needles.Max(n => n.Radius)) * 1.6);
            _canvas.Focus();
        };
    }

    private UIElement BuildUi()
    {
        var root = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
        var ok = new Button { Content = "ふたを置く", Padding = new Thickness(14, 4, 14, 4), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = "キャンセル", Padding = new Thickness(14, 4, 14, 4), IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var side = new StackPanel { Margin = new Thickness(10, 8, 10, 0) };
        side.Children.Add(Header("切り抜く元"));
        side.Children.Add(_sourceCombo);
        var sides = new StackPanel { Orientation = Orientation.Horizontal };
        sides.Children.Add(_day);
        sides.Children.Add(_night);
        side.Children.Add(sides);
        side.Children.Add(_showNeedle);
        side.Children.Add(_value);

        side.Children.Add(Header("形（丸・楕円・多角形を足し合わせて切り抜きます）"));
        side.Children.Add(_auto);
        side.Children.Add(_circle);
        side.Children.Add(_ellipse);
        side.Children.Add(_polygon);
        side.Children.Add(_delete);
        side.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 4, 0, 8),
            Text = "丸: 中をドラッグで移動、黄色い四角のドラッグか ＋／－ キーで大きさ。\n楕円: 右の四角で横、下の四角で縦の大きさ（＋／－ キーは両方）。\n多角形: クリックで点を足し、最初の点のクリック・ダブルクリック・Enter で閉じる（Esc でやめる、BackSpace で 1 点戻す）。点はドラッグで動かせます。\n選んだ形は矢印キーで 1px（Shift で 10px）動き、Delete で消えます。スペースを押しながら左ドラッグ（または右・中ドラッグ）で表示の移動、ホイールで拡大。",
        });

        side.Children.Add(Header("名前"));
        side.Children.Add(_name);
        side.Children.Add(Header("切り抜いた結果（昼・夜）"));
        var previews = new Grid();
        previews.ColumnDefinitions.Add(new ColumnDefinition());
        previews.ColumnDefinitions.Add(new ColumnDefinition());
        var dp = new Border { Background = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x54)), Margin = new Thickness(0, 0, 3, 0), Child = _dayPreview, Padding = new Thickness(4) };
        var np = new Border { Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x24)), Margin = new Thickness(3, 0, 0, 0), Child = _nightPreview, Padding = new Thickness(4) };
        Grid.SetColumn(np, 1);
        previews.Children.Add(dp);
        previews.Children.Add(np);
        side.Children.Add(previews);
        side.Children.Add(_status);

        var scroll = new ScrollViewer { Content = side, Width = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        DockPanel.SetDock(scroll, Dock.Right);
        root.Children.Add(scroll);
        root.Children.Add(_canvas);
        return root;
    }

    private static TextBlock Header(string t) => new() { Text = t, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 6, 0, 2) };

    private const string PsdItem = "PSD のレイヤーから選ぶ…";

    private void RefreshSources(int select)
    {
        _suppress = true;
        _sourceCombo.Items.Clear();
        foreach (var s in _sources) _sourceCombo.Items.Add(s.Label);
        if (_pickPsd != null) _sourceCombo.Items.Add(PsdItem);
        _sourceCombo.SelectedIndex = Math.Clamp(select, -1, _sources.Count - 1);
        _suppress = false;
        OnSourceChanged();
    }

    private void OnSourceChanged()
    {
        if (_suppress) return;
        int i = _sourceCombo.SelectedIndex;
        if (i == _sources.Count && _pickPsd != null)
        {
            var picked = _pickPsd();
            if (picked == null) { RefreshSources(_shownSource); return; }
            _sources.Add(picked);
            RefreshSources(_sources.Count - 1);
            return;
        }
        if (i < 0 || i >= _sources.Count) return;
        var src = _sources[i];
        if (!src.EnsureLoaded())
        {
            _status.Text = $"「{Path.GetFileName(src.DayPath)}」を読めません";
            return;
        }
        bool first = _shownSource < 0;
        _shownSource = i;
        _candidates = [];
        _candidate = -1;
        _night.IsEnabled = src.Night != null;
        if (src.Night == null) _day.IsChecked = true;
        ShowSide();
        // 初めて開いたときは、丸を自動で置く
        if (first && _canvas.Shapes.Count == 0) NextCandidate();
        UpdatePreview();
    }

    private CoverSource? Current => _shownSource >= 0 && _shownSource < _sources.Count ? _sources[_shownSource] : null;

    private void ShowSide()
    {
        if (Current is not { } src) return;
        bool night = _night.IsChecked == true && src.Night != null;
        _canvas.Night = night;
        _canvas.SetSource(night ? src.Night! : src.Day!, src.X, src.Y);
    }

    /// <summary>探す半径の上限（針の半径の 8 割、12〜160px）。</summary>
    private double MaxRadius => Math.Clamp(_needles.Select(n => n.Radius).DefaultIfEmpty(60).Max() * 0.8, 12, 160);

    /// <summary>丸の候補を順に当てる（1 回目は探す）。</summary>
    public void NextCandidate()
    {
        if (Current is not { Day: { } day } src || _needles.Count == 0) return;
        var c = _needles[0].Location;
        if (_candidate < 0 && _candidates.Count == 0)
        {
            // 画像の座標で探して、パネルの座標に戻す
            _candidates = CoverCircle.DetectAll(day, c.X - src.X, c.Y - src.Y, MaxRadius)
                .Select(r => r with { Cx = r.Cx + src.X, Cy = r.Cy + src.Y }).ToList();
        }
        if (_candidates.Count == 0)
        {
            if (!_canvas.Shapes.Any(s => s.IsCircle)) _canvas.SetCircle(CropShape.Circle(c.X, c.Y, Math.Round(MaxRadius * 0.4)));
            _status.Text = "軸のふたの丸が見つかりませんでした。針の中心に仮の丸を置いたので、大きさを合わせてください";
            UpdateButtons();
            return;
        }
        _candidate = (_candidate + 1) % _candidates.Count;
        var r = _candidates[_candidate];
        _canvas.SetCircle(CropShape.Circle(r.Cx, r.Cy, r.R));
        _status.Text = $"見つかった丸 {_candidates.Count} 個のうち {_candidate + 1} 番目（中心 {r.Cx:0.#}, {r.Cy:0.#}・半径 {r.R:0.#}）。"
            + (_candidates.Count > 1 ? "違っていたら「切り抜きの自動検出」をもう一度押すと、次の丸に切り替えます。" : "") + "手で直すこともできます";
        UpdateButtons();
    }

    private void AddCircle(bool ellipse)
    {
        var c = _needles.Count > 0 ? _needles[0].Location : _canvas.ViewCenter;
        double r = Math.Round(MaxRadius * 0.4);
        _canvas.AddShape(ellipse ? CropShape.Ellipse(c.X, c.Y, r, Math.Round(r * 0.7)) : CropShape.Circle(c.X, c.Y, r));
        _canvas.Focus();
    }

    private void UpdateButtons()
    {
        _delete.IsEnabled = _canvas.Selected != null;
    }

    private void UpdatePreview()
    {
        UpdateButtons();
        if (Current is not { } src) return;
        var shapes = _canvas.Shapes.Where(s => s.IsValid).ToList();
        var day = src.Cut(false, shapes);
        var night = src.Night != null ? src.Cut(true, shapes) : null;
        _dayPreview.Source = day != null ? ImageLoader.ToBitmap(day) : null;
        _nightPreview.Source = night != null ? ImageLoader.ToBitmap(night) : null;
        var shown = _canvas.Night ? night : day;
        var rect = CropShape.Bounds(shapes);
        _canvas.SetPreview(shown != null ? ImageLoader.ToBitmap(shown) : null, rect);
    }

    private void Accept()
    {
        var shapes = _canvas.Shapes.Where(s => s.IsValid).Select(s => s.Clone()).ToList();
        if (Current is not { } src || shapes.Count == 0)
        {
            MessageBox.Show(this, "切り抜く形（丸か多角形）を置いてください。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var name = _name.Text.Trim();
        Result = new CoverResult(src, shapes, name.Length > 0 ? name : "cover");
        DialogResult = true;
    }

    // ---- 確認用 ----
    public CoverResult ResultForTest() =>
        new(Current!, _canvas.Shapes.Where(s => s.IsValid).Select(s => s.Clone()).ToList(), _name.Text.Trim());
    public void AddPolygonForTest(IEnumerable<(double X, double Y)> pts) => _canvas.AddShape(CropShape.Polygon(pts));
    public string SourceLabelForTest => Current?.Label ?? "";
    public string AutoLabelForTest => _auto.Content as string ?? "";
    public void AddEllipseForTest(double cx, double cy, double rx, double ry) => _canvas.AddShape(CropShape.Ellipse(cx, cy, rx, ry));
    public int CandidateCountForTest => _candidates.Count;
}

/// <summary>ふたを作る画面の絵: 切り抜く元の画像・針・形を描き、形を動かす。座標はパネルの座標。</summary>
public sealed class CoverCanvas : FrameworkElement
{
    private BitmapSource? _image;
    private int _ix, _iy;
    private BitmapSource? _preview;
    private PsdRect _previewRect;
    public List<RenderItem> Needles { get; set; } = [];
    public bool ShowNeedle { get; set; }
    public bool Night { get; set; }
    /// <summary>針の値（0 = 最小、1 = 最大）。</summary>
    public double NeedleValue { get; set; }
    public List<CropShape> Shapes { get; } = [];
    public CropShape? Selected { get; private set; }

    public event Action? ShapesChanged;
    public event Action? SelectionChanged;
    public event Action<string>? StatusChanged;

    private double _zoom = 2;
    private Vector _offset;
    private enum Mode { None, Pan, Move, Radius, Vertex }
    private Mode _mode;
    private Point _startScreen, _startPanel, _mouse;
    private Vector _startOffset;
    private CropShape? _startShape;
    private int _vertex;
    private int _axis; // 大きさのつまみ: 0 = 丸（両方）、1 = 横、2 = 縦
    private bool _moved;
    private List<Point>? _poly;

    private static readonly Pen ShapePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x40, 0xE0, 0xFF)), 1.5));
    private static readonly Pen SelPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD8, 0x40)), 2));
    private static readonly Pen DrawPen = Freeze(new Pen(Brushes.White, 1.5) { DashStyle = DashStyles.Dash });
    private static readonly Pen CrossPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x50, 0x50)), 1));
    private static readonly Brush HandleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xD8, 0x40)));
    private static readonly Brush Back = Freeze(new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2E)));
    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    /// <summary>スペースキーを押しながら左ドラッグで表示を動かす。</summary>
    public SpacePan Space { get; }

    public CoverCanvas()
    {
        Space = new SpacePan(this);
        // 離したら、多角形を描いている途中なら十字に戻す
        Space.Changed += () => { if (!Space.Held && _poly != null) Cursor = Cursors.Cross; };
        Focusable = true;
        ClipToBounds = true;
    }

    public Point ToPanel(Point s) => new((s.X - _offset.X) / _zoom, (s.Y - _offset.Y) / _zoom);
    public Point ToScreen(Point p) => new(p.X * _zoom + _offset.X, p.Y * _zoom + _offset.Y);
    public Point ViewCenter => ToPanel(new Point(ActualWidth / 2, ActualHeight / 2));

    public void SetSource(PsdPixels px, int x, int y)
    {
        _image = ImageLoader.ToBitmap(px);
        _ix = x; _iy = y;
        InvalidateVisual();
    }

    public void SetPreview(BitmapSource? bmp, PsdRect rect)
    {
        _preview = bmp;
        _previewRect = rect;
        InvalidateVisual();
    }

    /// <summary>p を真ん中に、半径 r が画面に入るように表示する。</summary>
    public void CenterOn(Point p, double r)
    {
        double w = Math.Max(100, ActualWidth), h = Math.Max(100, ActualHeight);
        _zoom = Math.Clamp(Math.Min(w, h) / (2 * r), 0.1, 16);
        _offset = new Vector(w / 2 - p.X * _zoom, h / 2 - p.Y * _zoom);
        InvalidateVisual();
    }

    public void AddShape(CropShape s)
    {
        Shapes.Add(s);
        Select(s);
        ShapesChanged?.Invoke();
    }

    /// <summary>丸を置く（選んでいる丸か、最初の丸を置き換える。なければ足す）。</summary>
    public void SetCircle(CropShape c)
    {
        var old = Selected is { IsCircle: true } sel ? sel : Shapes.FirstOrDefault(s => s.IsCircle);
        if (old != null) Shapes[Shapes.IndexOf(old)] = c;
        else Shapes.Add(c);
        Select(c);
        ShapesChanged?.Invoke();
    }

    public void DeleteSelected()
    {
        if (Selected == null) return;
        Shapes.Remove(Selected);
        Select(null);
        ShapesChanged?.Invoke();
    }

    public void BeginPolygon()
    {
        _poly = [];
        Select(null);
        StatusChanged?.Invoke("多角形: クリックで点を足し、最初の点のクリック・ダブルクリック・Enter で閉じます（Esc でやめる）");
        Cursor = Cursors.Cross;
        InvalidateVisual();
    }

    private void EndPolygon(bool keep)
    {
        if (_poly == null) return;
        // ダブルクリックの 1 回目で足した点など、重なった点は 1 つにする
        var pts = new List<Point>();
        foreach (var p in _poly)
            if (pts.Count == 0 || (p - pts[^1]).Length * _zoom > 3) pts.Add(p);
        _poly = null;
        Cursor = null;
        if (keep && pts.Count >= 3) AddShape(CropShape.Polygon(pts.Select(p => (Math.Round(p.X, 1), Math.Round(p.Y, 1)))));
        else if (keep) StatusChanged?.Invoke("多角形は 3 点以上で閉じてください");
        InvalidateVisual();
    }

    private void Select(CropShape? s)
    {
        Selected = s;
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    // ---- 描画 ----

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Back, null, new Rect(RenderSize));
        if (_image == null) return;
        RenderOptions.SetBitmapScalingMode(this, _zoom >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        dc.DrawImage(_image, new Rect(ToScreen(new Point(_ix, _iy)), new Size(_image.PixelWidth * _zoom, _image.PixelHeight * _zoom)));

        if (ShowNeedle)
        {
            var view = new Matrix(_zoom, 0, 0, _zoom, _offset.X, _offset.Y);
            foreach (var n in Needles.OrderBy(n => n.Layer))
            {
                var bmp = Night ? n.Night ?? n.Day : n.Day;
                if (bmp == null) continue;
                var angle = n.TargetAngle(n.Minimum + NeedleValue * (n.Maximum - n.Minimum));
                dc.PushTransform(new MatrixTransform(n.NeedleMatrix(angle) * view));
                dc.DrawImage(bmp, new Rect(0, 0, bmp.PixelWidth, bmp.PixelHeight));
                dc.Pop();
            }
            // ふた（切り抜いた結果）を針の上に
            if (_preview != null && !_previewRect.IsEmpty)
                dc.DrawImage(_preview, new Rect(ToScreen(new Point(_previewRect.Left, _previewRect.Top)), new Size(_previewRect.Width * _zoom, _previewRect.Height * _zoom)));
        }

        // 針の回転の中心
        foreach (var n in Needles.DistinctBy(n => n.Location))
        {
            var c = ToScreen(n.Location);
            dc.DrawLine(CrossPen, c + new Vector(-6, 0), c + new Vector(6, 0));
            dc.DrawLine(CrossPen, c + new Vector(0, -6), c + new Vector(0, 6));
        }

        foreach (var s in Shapes)
        {
            bool sel = s == Selected;
            var pen = sel ? SelPen : ShapePen;
            if (s.IsCircle)
            {
                dc.DrawEllipse(null, pen, ToScreen(new Point(s.Cx, s.Cy)), s.R * _zoom, s.Ry * _zoom);
                if (sel)
                {
                    foreach (var (h, _) in SizeHandles(s)) dc.DrawRectangle(HandleBrush, null, new Rect(h.X - 4, h.Y - 4, 8, 8));
                    var c = ToScreen(new Point(s.Cx, s.Cy));
                    dc.DrawEllipse(HandleBrush, null, c, 2, 2);
                }
            }
            else
            {
                dc.DrawGeometry(null, pen, PolyGeometry(s.Points.Select(p => ToScreen(new Point(p.X, p.Y))).ToList(), true));
                if (sel)
                    foreach (var p in s.Points)
                    {
                        var sp = ToScreen(new Point(p.X, p.Y));
                        dc.DrawRectangle(HandleBrush, null, new Rect(sp.X - 3.5, sp.Y - 3.5, 7, 7));
                    }
            }
        }

        if (_poly != null && _poly.Count > 0)
        {
            var pts = _poly.Select(ToScreen).Append(_mouse).ToList();
            dc.DrawGeometry(null, DrawPen, PolyGeometry(pts, false));
            foreach (var p in _poly.Select(ToScreen)) dc.DrawRectangle(Brushes.White, null, new Rect(p.X - 3, p.Y - 3, 6, 6));
        }
    }

    private static StreamGeometry PolyGeometry(List<Point> pts, bool closed)
    {
        var g = new StreamGeometry();
        if (pts.Count < 2) return g;
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(pts[0], false, closed);
            ctx.PolyLineTo(pts.Skip(1).ToList(), true, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>大きさのつまみ（丸は右だけ、楕円は右で横・下で縦）。</summary>
    private IEnumerable<(Point At, int Axis)> SizeHandles(CropShape c)
    {
        yield return (ToScreen(new Point(c.Cx + c.R, c.Cy)), c.IsEllipse ? 1 : 0);
        if (c.IsEllipse) yield return (ToScreen(new Point(c.Cx, c.Cy + c.Ry)), 2);
    }

    private int HandleAt(Point screen) =>
        Selected is { IsCircle: true } c ? SizeHandles(c).Where(h => (h.At - screen).Length <= 7).Select(h => h.Axis + 1).FirstOrDefault() - 1 : -1;

    /// <summary>同じ種類（丸・楕円）のまま中心と半径を変えた形。</summary>
    private static CropShape Resized(CropShape s, double cx, double cy, double rx, double ry) =>
        s.IsEllipse ? CropShape.Ellipse(cx, cy, rx, ry) : CropShape.Circle(cx, cy, rx);

    // ---- マウス ----

    private CropShape? Hit(Point panel)
    {
        if (Selected != null && Selected.Contains(panel.X, panel.Y)) return Selected;
        return Shapes.Where(s => s.Contains(panel.X, panel.Y))
            .OrderBy(s => { var b = s.Bounds(); return b.Width * b.Height; }).FirstOrDefault();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (_image == null) return;
        _startScreen = _mouse = e.GetPosition(this);
        _startPanel = ToPanel(_startScreen);
        _startOffset = _offset;
        _moved = false;
        if (Space.IsPanButton(e.ChangedButton)) { _mode = Mode.Pan; Space.BeginPan(); CaptureMouse(); return; }
        if (e.ChangedButton != MouseButton.Left) return;

        if (_poly != null)
        {
            if (e.ClickCount >= 2) { EndPolygon(true); return; }
            if (_poly.Count >= 3 && (ToScreen(_poly[0]) - _startScreen).Length <= 8) { EndPolygon(true); return; }
            _poly.Add(_startPanel);
            InvalidateVisual();
            return;
        }

        if (Selected is { IsCircle: true } c && HandleAt(_startScreen) is >= 0 and var axis)
        {
            _mode = Mode.Radius;
            _axis = axis;
            _startShape = c.Clone();
            CaptureMouse();
            return;
        }
        if (Selected is { IsCircle: false } poly)
            for (int i = 0; i < poly.Points.Count; i++)
                if ((ToScreen(new Point(poly.Points[i].X, poly.Points[i].Y)) - _startScreen).Length <= 6)
                {
                    _mode = Mode.Vertex;
                    _vertex = i;
                    _startShape = poly.Clone();
                    CaptureMouse();
                    return;
                }
        var hit = Hit(_startPanel);
        if (hit != Selected) Select(hit);
        if (hit != null)
        {
            _mode = Mode.Move;
            _startShape = hit.Clone();
            CaptureMouse();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var sp = e.GetPosition(this);
        _mouse = sp;
        if (_poly != null) { InvalidateVisual(); return; }
        if (_mode == Mode.None)
        {
            int ha = HandleAt(sp);
            Cursor = Space.Held || Space.Panning ? SpacePan.Cursor : ha == 2 ? Cursors.SizeNS : ha >= 0 ? Cursors.SizeWE : Hit(ToPanel(sp)) != null ? Cursors.SizeAll : null;
            return;
        }
        if ((sp - _startScreen).Length > 2) _moved = true;
        if (!_moved) return;
        var d = ToPanel(sp) - _startPanel;
        double dx = Math.Round(d.X * 2) / 2, dy = Math.Round(d.Y * 2) / 2;
        switch (_mode)
        {
            case Mode.Pan:
                _offset = _startOffset + (sp - _startScreen);
                break;
            case Mode.Move when Selected != null && _startShape != null:
                Replace(_startShape.Offset(dx, dy));
                break;
            case Mode.Radius when Selected != null && _startShape != null:
                {
                    var p = ToPanel(sp);
                    double ddx = p.X - _startShape.Cx, ddy = p.Y - _startShape.Cy;
                    static double Half(double v) => Math.Max(1, Math.Round(v * 2) / 2);
                    double rx = _axis == 2 ? _startShape.R : Half(_axis == 1 ? Math.Abs(ddx) : Math.Sqrt(ddx * ddx + ddy * ddy));
                    double ry = _axis == 1 ? _startShape.Ry : _axis == 2 ? Half(Math.Abs(ddy)) : rx;
                    Replace(Resized(_startShape, _startShape.Cx, _startShape.Cy, rx, ry));
                    StatusChanged?.Invoke(_startShape.IsEllipse ? $"横の半径 {rx:0.#}・縦の半径 {ry:0.#}" : $"半径 {rx:0.#}");
                    break;
                }
            case Mode.Vertex when Selected != null && _startShape != null:
                {
                    var pts = _startShape.Points.ToList();
                    pts[_vertex] = (pts[_vertex].X + dx, pts[_vertex].Y + dy);
                    Replace(CropShape.Polygon(pts));
                    break;
                }
        }
        InvalidateVisual();
    }

    /// <summary>選んでいる形を置き換える（選んだまま）。</summary>
    private void Replace(CropShape s)
    {
        if (Selected == null) return;
        int i = Shapes.IndexOf(Selected);
        if (i < 0) return;
        Shapes[i] = s;
        Selected = s;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_mode == Mode.None) return;
        var mode = _mode;
        _mode = Mode.None;
        ReleaseMouseCapture();
        Space.EndPan();
        if (mode != Mode.Pan && _moved) ShapesChanged?.Invoke();
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        Space.EndPan();
        if (_mode is Mode.Move or Mode.Radius or Mode.Vertex && _moved) ShapesChanged?.Invoke();
        _mode = Mode.None;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var sp = e.GetPosition(this);
        var p = ToPanel(sp);
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.2 : 1 / 1.2), 0.1, 32);
        _offset = new Vector(sp.X - p.X * _zoom, sp.Y - p.Y * _zoom);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_poly != null)
        {
            if (e.Key == Key.Escape) { EndPolygon(false); StatusChanged?.Invoke("多角形をやめました"); e.Handled = true; }
            else if (e.Key == Key.Enter) { EndPolygon(true); e.Handled = true; }
            else if (e.Key == Key.Back && _poly.Count > 0) { _poly.RemoveAt(_poly.Count - 1); InvalidateVisual(); e.Handled = true; }
            return;
        }
        if (Selected == null) return;
        if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; return; }
        double k = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        var d = e.Key switch { Key.Left => (-k, 0.0), Key.Right => (k, 0.0), Key.Up => (0.0, -k), Key.Down => (0.0, k), _ => (0.0, 0.0) };
        if (d != (0, 0))
        {
            Replace(Selected.Offset(d.Item1, d.Item2));
            ShapesChanged?.Invoke();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        int step = e.Key is Key.OemPlus or Key.Add ? 1 : e.Key is Key.OemMinus or Key.Subtract ? -1 : 0;
        if (step != 0 && Selected.IsCircle)
        {
            double d0 = step * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 5 : 0.5);
            double rx = Math.Max(1, Selected.R + d0), ry = Math.Max(1, Selected.Ry + d0);
            Replace(Resized(Selected, Selected.Cx, Selected.Cy, rx, ry));
            StatusChanged?.Invoke(Selected.IsEllipse
                ? $"横の半径 {rx.ToString("0.#", CultureInfo.InvariantCulture)}・縦の半径 {ry.ToString("0.#", CultureInfo.InvariantCulture)}"
                : $"半径 {rx.ToString("0.#", CultureInfo.InvariantCulture)}");
            ShapesChanged?.Invoke();
            InvalidateVisual();
            e.Handled = true;
        }
    }
}
