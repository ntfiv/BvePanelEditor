using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BvePanelEditor.Core;
using BvePanelEditor.Core.Psd;
using BvePanelEditor.Rendering;

namespace BvePanelEditor.Views;

/// <summary>
/// パネル本体の表示範囲（Left・Top・Right・Bottom）を、画像の描かれている範囲から余白を引いて計算する画面。
/// 余白は上下左右ごとに変えられ、見本の赤い枠の辺をドラッグしても変えられる。
/// </summary>
public sealed class ViewLimitWindow : Window
{
    private readonly PsdPixels? _day, _night;
    private readonly double _screenW, _screenH;
    private readonly int _defaultMargin;
    private readonly ComboBox _target = new() { Width = 150 };
    private readonly CheckBox _useColor = new() { Content = "まわりを埋めている一定の色も余白とみなす", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider _tolerance = new() { Minimum = 0, Maximum = 64, Value = 12, Width = 120, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _toleranceText = new() { Width = 26, VerticalAlignment = VerticalAlignment.Center };

    // 余白（左・上・右・下）
    private readonly int[] _m = new int[4];
    private readonly TextBox[] _boxes = new TextBox[4];
    private readonly CheckBox _link = new() { Content = "上下左右そろえる", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private readonly Slider _all = new() { Minimum = 0, Maximum = 400, Width = 140, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
    private static readonly string[] SideNames = ["左", "上", "右", "下"];
    private bool _suppress;

    // 見本（画像の上に枠を重ねる。Viewbox で窓に合わせて縮める）
    private readonly Grid _stage = new();
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Canvas _overlay = new() { Background = Brushes.Transparent };
    private readonly Rectangle _drawnRect = new() { Stroke = Brushes.LimeGreen, StrokeThickness = 2 };
    private readonly Rectangle _resultRect = new() { Stroke = Brushes.Red, StrokeThickness = 2, StrokeDashArray = [4, 3] };
    private PsdPixels? _shown;
    private double _scale = 1;
    private int _dragSides; // ドラッグ中の辺（ビット: 1 左・2 上・4 右・8 下）

    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _warn = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick };
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
    private readonly Button _ok = new() { Content = "表示範囲に入れる", Width = 130, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
    private PanelBoundsResult? _d, _n;

    /// <summary>描かれている範囲（余白を引く前）。</summary>
    public PsdRect DrawnArea { get; private set; }
    /// <summary>［表示範囲に入れる］で決まった範囲。</summary>
    public PsdRect Result { get; private set; }
    /// <summary>今の余白（左・上・右・下）。</summary>
    public IReadOnlyList<int> Margins => _m;

    /// <param name="defaultMargin">余白の初期値（px）。</param>
    /// <param name="screenW">ズーム 1 倍の画面の大きさ（なければ 0）。表示範囲がこれより狭いと知らせる。</param>
    public ViewLimitWindow(Window owner, PsdPixels? day, PsdPixels? night, int defaultMargin, double screenW, double screenH)
    {
        Owner = owner;
        Title = "描かれている範囲から表示範囲を計算";
        Width = 1000; Height = 900;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Yu Gothic UI");
        FontSize = 12;
        Background = SystemColors.WindowBrush;
        _day = day; _night = night;
        _screenW = screenW; _screenH = screenH;
        _defaultMargin = Math.Max(0, defaultMargin);
        Array.Fill(_m, _defaultMargin);

        var items = new List<string>();
        // 夜の画像は暗い所が多く、暗い絵を余白の色と見誤りやすいので、昼を基準にする
        if (day != null) items.Add("昼の画像");
        if (night != null) items.Add("夜の画像");
        if (day != null && night != null) items.Add("昼と夜の重なり");
        _target.ItemsSource = items;
        _target.SelectedIndex = 0;

        var root = new DockPanel { Margin = new Thickness(10) };

        // 1 行目: 描かれている範囲の見つけ方
        var opts = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        opts.Children.Add(Label("対象"));
        opts.Children.Add(_target);
        opts.Children.Add(new Border { Width = 16 });
        opts.Children.Add(_useColor);
        opts.Children.Add(new Border { Width = 16 });
        opts.Children.Add(Label("色の許容差"));
        opts.Children.Add(_tolerance);
        opts.Children.Add(_toleranceText);
        _tolerance.ToolTip = "余白の色とどれだけ違えば「描かれている」とみなすか（0〜64）";
        DockPanel.SetDock(opts, Dock.Top);
        root.Children.Add(opts);

        // 2 行目: 余白
        var margins = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        margins.Children.Add(new TextBlock { Text = "余白（px）", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        margins.Children.Add(_link);
        for (int i = 0; i < 4; i++) margins.Children.Add(NumberBox(i));
        margins.Children.Add(new Border { Width = 12 });
        margins.Children.Add(Label("まとめて"));
        margins.Children.Add(_all);
        var reset = new Button { Content = "揺れの余白に戻す", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(8, 0, 0, 0), ToolTip = $"上下左右とも {_defaultMargin}px（「揺れの余白（目安）」の大きさ）にします" };
        reset.Click += (_, _) => SetAll(_defaultMargin);
        var zero = new Button { Content = "0 にする", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 0, 0, 0) };
        zero.Click += (_, _) => SetAll(0);
        margins.Children.Add(reset);
        margins.Children.Add(zero);
        _all.ToolTip = "上下左右の余白をまとめて変えます";
        _link.ToolTip = "オンのときは、どれか 1 つを変えると上下左右とも同じ値になります。赤い枠の辺をドラッグしたときも同じです";
        DockPanel.SetDock(margins, Dock.Top);
        root.Children.Add(margins);

        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        buttons.Children.Add(_ok);
        buttons.Children.Add(new Button { Content = "キャンセル", Width = 90, IsCancel = true });
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        var texts = new StackPanel();
        texts.Children.Add(_info);
        texts.Children.Add(_warn);
        texts.Children.Add(_note);
        texts.Children.Add(new TextBlock
        {
            Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
            Text = "緑の枠: 描かれている範囲（透明な所と、まわりを埋めている一定の色を除く）。赤の枠: 余白を引いた表示範囲（[This] の Left・Top・Right・Bottom）。赤の枠の辺や角をドラッグして余白を変えられます。数字の欄はホイール・↑↓ キーで 1px（Shift で 10px）ずつ変わります。",
        });
        bottom.Children.Add(texts);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        _stage.Children.Add(_image);
        _overlay.Children.Add(_drawnRect);
        _overlay.Children.Add(_resultRect);
        _stage.Children.Add(_overlay);
        root.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2E)), Padding = new Thickness(4),
            Child = new Viewbox { Child = _stage, Stretch = Stretch.Uniform },
        });
        Content = root;

        _overlay.MouseMove += OnOverlayMove;
        _overlay.MouseLeftButtonDown += OnOverlayDown;
        _overlay.MouseLeftButtonUp += (_, _) => { _dragSides = 0; _overlay.ReleaseMouseCapture(); };
        _target.SelectionChanged += (_, _) => Recalc();
        _useColor.Click += (_, _) => Recalc();
        _tolerance.ValueChanged += (_, _) => Recalc();
        _all.ValueChanged += (_, _) => { if (!_suppress) SetAll((int)_all.Value); };
        _link.Click += (_, _) => { if (_link.IsChecked == true) SetAll(_m[0]); };
        _ok.Click += (_, _) => { if (!Result.IsEmpty) DialogResult = true; };
        Loaded += (_, _) => Recalc();
        SizeChanged += (_, _) => PlaceRects();
        ContentRendered += (_, _) => PlaceRects();
    }

    private static TextBlock Label(string t) => new() { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };

    /// <summary>余白 1 辺の数字の欄（▲▼・ホイール・↑↓ キー）。</summary>
    private FrameworkElement NumberBox(int side)
    {
        var box = new TextBox { Width = 48, VerticalContentAlignment = VerticalAlignment.Center, Text = _m[side].ToString() };
        _boxes[side] = box;
        void Step(int d) => SetSide(side, _m[side] + d * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1));
        box.TextChanged += (_, _) =>
        {
            if (_suppress || !int.TryParse(box.Text.Trim(), out var v)) return;
            SetSide(side, v, fromBox: true);
        };
        box.PreviewMouseWheel += (_, e) => { Step(e.Delta > 0 ? 1 : -1); e.Handled = true; };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Up) { Step(1); e.Handled = true; }
            else if (e.Key == Key.Down) { Step(-1); e.Handled = true; }
        };
        var up = new RepeatButton { Content = "▲", FontSize = 7, Width = 16, Height = 11, Padding = new Thickness(0) };
        var down = new RepeatButton { Content = "▼", FontSize = 7, Width = 16, Height = 11, Padding = new Thickness(0) };
        up.Click += (_, _) => Step(1);
        down.Click += (_, _) => Step(-1);
        var spin = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        spin.Children.Add(up);
        spin.Children.Add(down);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 0) };
        panel.Children.Add(new TextBlock { Text = SideNames[side], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        panel.Children.Add(box);
        panel.Children.Add(spin);
        return panel;
    }

    /// <summary>余白 1 辺を変える（そろえるがオンなら 4 辺とも）。表示範囲が画像の外に出ない・裏返らないようにする。</summary>
    private void SetSide(int side, int value, bool fromBox = false)
    {
        if (_link.IsChecked == true) { SetAll(value, fromBox ? side : -1); return; }
        _m[side] = ClampSide(side, value);
        UpdateResult(fromBox ? side : -1);
    }

    private void SetAll(int value, int skipBox = -1)
    {
        for (int i = 0; i < 4; i++) _m[i] = value;
        for (int i = 0; i < 4; i++) _m[i] = ClampSide(i, _m[i]);
        UpdateResult(skipBox);
    }

    /// <summary>余白の範囲: 画像の端まで広げられ（負の値）、反対の辺を越えない。</summary>
    private int ClampSide(int side, int v)
    {
        if (DrawnArea.IsEmpty || _shown == null) return Math.Max(0, v);
        var img = _shown.Bounds;
        var a = DrawnArea;
        int lo = side switch { 0 => img.Left - a.Left, 1 => img.Top - a.Top, 2 => a.Right - img.Right, _ => a.Bottom - img.Bottom };
        int opposite = side switch { 0 => _m[2], 1 => _m[3], 2 => _m[0], _ => _m[1] };
        int size = side is 0 or 2 ? a.Width : a.Height;
        int hi = size - opposite - 1;
        return Math.Clamp(v, Math.Min(lo, 0), Math.Max(Math.Min(lo, 0), hi));
    }

    /// <summary>描かれている範囲を見つけ直す（見つけ方を変えたとき）。</summary>
    private void Recalc()
    {
        if (!IsLoaded) return;
        int tol = (int)_tolerance.Value;
        _toleranceText.Text = tol.ToString();
        bool useColor = _useColor.IsChecked == true;
        var target = _target.SelectedItem as string ?? "";
        _d = _day != null && target != "夜の画像" ? PanelBounds.Detect(_day, tol, useFillColor: useColor) : null;
        _n = _night != null && target != "昼の画像" ? PanelBounds.Detect(_night, tol, useFillColor: useColor) : null;
        DrawnArea = PanelBounds.Overlap(_d?.Content ?? default, _n?.Content ?? default);
        var img = _d != null ? _day! : _night!;
        if (!ReferenceEquals(img, _shown)) ShowImage(img);
        _all.Maximum = Math.Max(10, Math.Min(DrawnArea.Width, DrawnArea.Height) / 2);
        for (int i = 0; i < 4; i++) _m[i] = ClampSide(i, _m[i]);
        UpdateResult();
    }

    /// <summary>余白から表示範囲を出し直す（見本の枠と文字も）。</summary>
    private void UpdateResult(int skipBox = -1)
    {
        var a = DrawnArea;
        var r = new PsdRect(a.Left + _m[0], a.Top + _m[1], a.Right - _m[2], a.Bottom - _m[3]);
        if (_shown != null) r = r.Intersect(_shown.Bounds);
        Result = a.IsEmpty || r.Right <= r.Left || r.Bottom <= r.Top ? default : r;

        _suppress = true;
        for (int i = 0; i < 4; i++) if (i != skipBox && _boxes[i].Text != _m[i].ToString()) _boxes[i].Text = _m[i].ToString();
        if (_m.All(v => v == _m[0]) && _m[0] >= 0) _all.Value = Math.Min(_all.Maximum, _m[0]);
        _suppress = false;

        string Bg(PanelBoundsResult? res, string label)
        {
            if (res == null) return "";
            var parts = new List<string>();
            if (res.TransparentEdge) parts.Add("透明");
            parts.AddRange(res.Background.Select(c => $"#{c.R:X2}{c.G:X2}{c.B:X2}"));
            return $"{label}: {(parts.Count > 0 ? string.Join("・", parts) : "なし（縁まで描かれている）")}";
        }
        string Rect(PsdRect x) => x.IsEmpty ? "（なし）" : $"左 {x.Left}・上 {x.Top}・右 {x.Right}・下 {x.Bottom}（{x.Width}×{x.Height}）";
        string marginText = _m.All(v => v == _m[0]) ? $"{_m[0]}px" : $"左 {_m[0]}・上 {_m[1]}・右 {_m[2]}・下 {_m[3]}px";
        _info.Text = $"描かれている範囲: {Rect(DrawnArea)}\n表示範囲（余白 {marginText} を引く）: {Rect(Result)}\n余白とみなしたもの　{string.Join("　", new[] { Bg(_d, "昼"), Bg(_n, "夜") }.Where(x => x.Length > 0))}";
        var warns = new List<string>();
        if (DrawnArea.IsEmpty) warns.Add("描かれている所が見つかりません。許容差を小さくするか、「一定の色も余白とみなす」を外してください。");
        else if (Result.IsEmpty) warns.Add("余白が大きすぎて、表示範囲がなくなります。");
        if (_m.Any(v => v < 0)) warns.Add("余白が負の値の辺は、描かれている範囲より外側に広げています（何も描かれていない所が画面に入るかもしれません）。");
        _warn.Text = string.Join("\n", warns);
        _note.Text = !Result.IsEmpty && _screenW > 0 && (Result.Width < _screenW || Result.Height < _screenH)
            ? $"参考: 表示範囲は、ズーム 1 倍の画面の目安（{Math.Round(_screenW)}×{Math.Round(_screenH)}）より狭いです。" : "";
        _ok.IsEnabled = !Result.IsEmpty;
        PlaceRects();
    }

    /// <summary>見本の画像を作る（市松模様の上に画像。大きい画像は縮める）。</summary>
    private void ShowImage(PsdPixels img)
    {
        _shown = img;
        _scale = Math.Min(1, 1400.0 / Math.Max(img.Width, img.Height));
        int w = Math.Max(1, (int)(img.Width * _scale)), h = Math.Max(1, (int)(img.Height * _scale));
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var checker = new DrawingBrush(new GeometryDrawing(Brushes.Gray, null, new RectangleGeometry(new System.Windows.Rect(0, 0, 8, 8))))
            { TileMode = TileMode.Tile, Viewport = new System.Windows.Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
            dc.DrawRectangle(Brushes.DimGray, null, new System.Windows.Rect(0, 0, w, h));
            dc.DrawRectangle(checker, null, new System.Windows.Rect(0, 0, w, h));
            dc.DrawImage(ImageLoader.ToBitmap(img), new System.Windows.Rect(0, 0, w, h));
        }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        _image.Source = bmp;
        _stage.Width = w; _stage.Height = h;
        _overlay.Width = w; _overlay.Height = h;
    }

    private void PlaceRects()
    {
        void Place(Rectangle shape, PsdRect r)
        {
            shape.Visibility = r.IsEmpty || _shown == null ? Visibility.Collapsed : Visibility.Visible;
            if (r.IsEmpty || _shown == null) return;
            Canvas.SetLeft(shape, (r.Left - _shown.Bounds.Left) * _scale);
            Canvas.SetTop(shape, (r.Top - _shown.Bounds.Top) * _scale);
            shape.Width = Math.Max(1, r.Width * _scale);
            shape.Height = Math.Max(1, r.Height * _scale);
        }
        Place(_drawnRect, DrawnArea);
        Place(_resultRect, Result);
        // 線の太さは画面の上でおよそ 2px に見えるように
        double zoom = _stage.ActualWidth > 0 && _stage.Parent is Viewbox vb && vb.ActualWidth > 0 ? vb.ActualWidth / _stage.ActualWidth : 1;
        _drawnRect.StrokeThickness = _resultRect.StrokeThickness = 2 / Math.Max(0.05, zoom);
    }

    // ---- 赤い枠の辺をドラッグ ----

    /// <summary>マウスの近くにある赤い枠の辺（ビット: 1 左・2 上・4 右・8 下）。</summary>
    private int SidesNear(Point p)
    {
        if (Result.IsEmpty || _shown == null) return 0;
        double zoom = _stage.Parent is Viewbox vb && vb.ActualWidth > 0 && _stage.ActualWidth > 0 ? vb.ActualWidth / _stage.ActualWidth : 1;
        double tol = 7 / Math.Max(0.05, zoom); // 画面の上で 7px
        double l = (Result.Left - _shown.Bounds.Left) * _scale, t = (Result.Top - _shown.Bounds.Top) * _scale;
        double r = (Result.Right - _shown.Bounds.Left) * _scale, b = (Result.Bottom - _shown.Bounds.Top) * _scale;
        bool inY = p.Y > t - tol && p.Y < b + tol, inX = p.X > l - tol && p.X < r + tol;
        int s = 0;
        if (inY && Math.Abs(p.X - l) <= tol) s |= 1;
        if (inX && Math.Abs(p.Y - t) <= tol) s |= 2;
        if (inY && Math.Abs(p.X - r) <= tol) s |= 4;
        if (inX && Math.Abs(p.Y - b) <= tol) s |= 8;
        return s;
    }

    private static Cursor CursorFor(int sides) => sides switch
    {
        1 or 4 => Cursors.SizeWE,
        2 or 8 => Cursors.SizeNS,
        3 or 12 => Cursors.SizeNWSE,
        6 or 9 => Cursors.SizeNESW,
        _ => Cursors.Arrow,
    };

    private void OnOverlayDown(object sender, MouseButtonEventArgs e)
    {
        _dragSides = SidesNear(e.GetPosition(_overlay));
        if (_dragSides != 0) { _overlay.CaptureMouse(); e.Handled = true; }
    }

    private void OnOverlayMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(_overlay);
        if (_dragSides == 0) { _overlay.Cursor = CursorFor(SidesNear(p)); return; }
        if (_shown == null || DrawnArea.IsEmpty) return;
        int x = (int)Math.Round(p.X / _scale) + _shown.Bounds.Left, y = (int)Math.Round(p.Y / _scale) + _shown.Bounds.Top;
        var a = DrawnArea;
        // 辺の位置 → 余白
        var want = new (int Side, int Value)[]
        {
            (0, x - a.Left), (1, y - a.Top), (2, a.Right - x), (3, a.Bottom - y),
        };
        if (_link.IsChecked == true)
        {
            // そろえるときは、動かした辺の余白を 4 辺ともに
            var first = want.First(w => (_dragSides & (1 << w.Side)) != 0);
            SetAll(first.Value);
        }
        else
        {
            foreach (var (side, value) in want)
                if ((_dragSides & (1 << side)) != 0) _m[side] = ClampSide(side, value);
            UpdateResult();
        }
    }

    // ---- 確認用 ----

    /// <summary>確認用: 今の説明文。</summary>
    public string InfoForTest => string.Join("\n", new[] { _info.Text, _warn.Text, _note.Text }.Where(t => t.Length > 0));

    /// <summary>確認用: 余白を設定する（link が null なら今のまま）。</summary>
    public void SetMarginsForTest(bool? link, params int[] values)
    {
        if (link is { } l) _link.IsChecked = l;
        if (values.Length == 1) SetAll(values[0]);
        else for (int i = 0; i < 4 && i < values.Length; i++) SetSide(i, values[i]);
    }
}
