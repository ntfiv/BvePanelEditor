using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BvePanelEditor.Core;
using BvePanelEditor.Core.Imaging;
using BvePanelEditor.Core.Psd;
using BvePanelEditor.Rendering;

namespace BvePanelEditor.Views;

/// <summary>
/// 画像を 4 隅で変形して貼りこむ位置を決める（モニタくんの「変形とパネル上の位置」と同じ操作）。
/// パネルの絵を敷き、変形した画像を重ねて見せる。4 隅をドラッグ（クリックした隅は矢印キーで 1 画素、Shift で 10 画素）、
/// 形の中をドラッグすると 4 隅をまとめて動かす。
/// </summary>
public sealed class WarpPlaceWindow : Window
{
    private readonly WarpView _view;
    private readonly TextBox[] _corner = new TextBox[8];
    private readonly Slider _opacity = new() { Minimum = 0.1, Maximum = 1, Value = 0.85, Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly PsdPixels _source;
    private readonly int _interval;
    private readonly Point[] _initial;
    private bool _sync;

    /// <summary>決めた 4 隅（左上・右上・右下・左下、パネルの座標）。</summary>
    public Point[] Corners { get; } = new Point[4];

    internal BitmapSource? PanelBitmap { get; }
    internal BitmapSource? Warped { get; private set; }
    internal Point WarpedAt { get; private set; }
    internal double PreviewOpacity => _opacity.Value;

    /// <param name="panel">敷くパネルの絵（パネルの座標 (0, 0) が左上）。</param>
    /// <param name="source">貼りこむ画像。</param>
    /// <param name="interval">DigitalNumber のときの 1 コマの高さ（0 なら常点灯。見せるのは最初のコマ）。</param>
    /// <param name="center">最初に置く場所の中心（パネルの座標）。</param>
    /// <param name="corners">今の 4 隅（変形し直すとき）。null なら center に変形なしで置く。</param>
    public WarpPlaceWindow(Window owner, string title, BitmapSource? panel, PsdPixels source, int interval, Point center, Point[]? corners = null)
    {
        if (corners is { Length: 4 }) center = new Point(corners.Average(p => p.X), corners.Average(p => p.Y));
        Owner = owner;
        Title = title;
        Width = 1100; Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        PanelBitmap = panel;
        _source = source;
        _interval = interval;

        // 最初は変形なし（元の大きさ）。パネルより大きければ、パネルの半分に収まるよう縮める
        double w = source.Width, h = interval > 0 ? interval : source.Height;
        if (panel != null)
        {
            double k = Math.Min(1, Math.Min(panel.PixelWidth * 0.5 / w, panel.PixelHeight * 0.5 / h));
            w *= k; h *= k;
        }
        double x0 = Math.Round(center.X - w / 2), y0 = Math.Round(center.Y - h / 2);
        _initial = [new(x0, y0), new(x0 + Math.Round(w), y0), new(x0 + Math.Round(w), y0 + Math.Round(h)), new(x0, y0 + Math.Round(h))];
        (corners is { Length: 4 } ? corners : _initial).CopyTo(Corners, 0);

        _view = new WarpView(this);
        for (int i = 0; i < 8; i++)
        {
            int idx = i;
            var tb = new TextBox { Width = 72, Margin = new Thickness(0, 0, 6, 0) };
            tb.LostFocus += (_, _) => CommitField(idx);
            tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitField(idx); e.Handled = true; } };
            _corner[i] = tb;
        }
        _opacity.ValueChanged += (_, _) => _view.InvalidateVisual();

        var side = new StackPanel { Margin = new Thickness(10), Width = 260 };
        side.Children.Add(Header("4 隅（パネルの座標）"));
        string[] names = ["左上", "右上", "右下", "左下"];
        for (int i = 0; i < 4; i++)
            side.Children.Add(Row(names[i], Pair(_corner[i * 2], _corner[i * 2 + 1])));
        var reset = new Button { Content = "変形なしに戻す", Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) => Edit(() => _initial.CopyTo(Corners, 0));
        side.Children.Add(reset);
        side.Children.Add(Row("重ねる絵の濃さ", _opacity));
        var fit = new Button { Content = "貼りこむ所を中心に表示する", Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        fit.Click += (_, _) => _view.FitQuad();
        side.Children.Add(fit);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 24, 0, 0) };
        var ok = new Button { Content = "貼りこむ", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => { if (TryBuildTransform() != null) DialogResult = true; };
        var cancel = new Button { Content = "やめる", IsCancel = true, MinWidth = 80 };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        side.Children.Add(buttons);

        var dock = new DockPanel();
        var scroll = new ScrollViewer { Content = side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        DockPanel.SetDock(scroll, Dock.Right);
        dock.Children.Add(scroll);
        var viewGrid = new Grid();
        viewGrid.Children.Add(_view);
        viewGrid.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(6, 2, 6, 2), CornerRadius = new CornerRadius(3), IsHitTestVisible = false,
            Background = new SolidColorBrush(Color.FromArgb(0x90, 0x20, 0x20, 0x24)),
            Child = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF)), FontSize = 11, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right,
                Text = "スペースキー or マウス中ボタン＋ドラッグで画面移動、マウスホイールで拡大縮小、Ctrl+0 で全体表示、Ctrl+1 で等倍表示",
            },
        });
        dock.Children.Add(viewGrid);
        Content = dock;

        SyncFields();
        Loaded += (_, _) => { UpdateWarp(); _view.FitQuad(); _view.Focus(); };
    }

    private static TextBlock Header(string s) => new() { Text = s, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };

    private static FrameworkElement Row(string label, UIElement c)
    {
        var dp = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        dp.Children.Add(new TextBlock { Text = label, Width = 90, VerticalAlignment = VerticalAlignment.Center });
        dp.Children.Add(c);
        return dp;
    }

    private static StackPanel Pair(UIElement a, UIElement b)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(a); sp.Children.Add(b);
        return sp;
    }

    private void CommitField(int idx)
    {
        if (_sync) return;
        if (!double.TryParse(_corner[idx].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) { SyncFields(); return; }
        var p = Corners[idx / 2];
        var np = idx % 2 == 0 ? new Point(v, p.Y) : new Point(p.X, v);
        if (np == p) return;
        Edit(() => Corners[idx / 2] = np);
    }

    internal void Edit(Action a)
    {
        a();
        SyncFields();
        UpdateWarp();
    }

    private void SyncFields()
    {
        _sync = true;
        for (int i = 0; i < 4; i++)
        {
            _corner[i * 2].Text = PanelValues.Num(Corners[i].X);
            _corner[i * 2 + 1].Text = PanelValues.Num(Corners[i].Y);
        }
        _sync = false;
    }

    /// <summary>今の 4 隅の変換（決められなければ知らせて null）。</summary>
    private Homography? TryBuildTransform()
    {
        try
        {
            var h = Homography.FromRectToQuad(_source.Width, _interval > 0 ? _interval : _source.Height,
                (Corners[0].X, Corners[0].Y), (Corners[1].X, Corners[1].Y), (Corners[2].X, Corners[2].Y), (Corners[3].X, Corners[3].Y));
            h.Inverse();
            return h;
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    internal void UpdateWarp()
    {
        try
        {
            var src = _interval > 0 ? Warp.Crop(_source, 0, 0, _source.Width, _interval) : _source;
            var r = Warp.ToQuad(src, (Corners[0].X, Corners[0].Y), (Corners[1].X, Corners[1].Y), (Corners[2].X, Corners[2].Y), (Corners[3].X, Corners[3].Y));
            Warped = r.Width > 0 && r.Height > 0 ? ImageLoader.ToBitmap(r) : null;
            WarpedAt = new Point(r.Bounds.Left, r.Bounds.Top);
        }
        catch (InvalidOperationException) { Warped = null; }
        _view.InvalidateVisual();
    }

    /// <summary>確認用: 4 隅を決めて閉じる（ShowDialog の代わり）。</summary>
    internal void SetCornersForTest(Point[] corners) => Edit(() => corners.CopyTo(Corners, 0));

    /// <summary>4 隅で囲んだ形の中か（パネルの座標）。</summary>
    internal bool InsideQuad(Point q)
    {
        bool inside = false;
        for (int i = 0, j = 3; i < 4; j = i++)
        {
            var a = Corners[i]; var b = Corners[j];
            if ((a.Y > q.Y) != (b.Y > q.Y) && q.X < (b.X - a.X) * (q.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }
}

/// <summary>パネルの画素で描く表示面（拡大・ドラッグ）。</summary>
internal sealed class WarpView : FrameworkElement
{
    private readonly WarpPlaceWindow _w;
    private double _zoom = 1;
    private Vector _pan = new(20, 20);
    private int _dragCorner = -1, _selCorner = -1;
    private bool _dragMove, _panning;
    private Point _last;
    private static readonly Pen QuadPen = MakePen(Color.FromRgb(0x20, 0x90, 0xFF), 1);
    private static readonly Brush Back = MakeBack();

    public WarpView(WarpPlaceWindow w)
    {
        _w = w;
        Focusable = true;
        ClipToBounds = true;
    }

    private static Pen MakePen(Color c, double t) { var p = new Pen(new SolidColorBrush(c), t); p.Freeze(); return p; }
    private static Brush MakeBack() { var b = new SolidColorBrush(Color.FromRgb(0x5A, 0x5E, 0x66)); b.Freeze(); return b; }

    private Point ToScreen(Point p) => new(p.X * _zoom + _pan.X, p.Y * _zoom + _pan.Y);
    private Point ToPanel(Point s) => new((s.X - _pan.X) / _zoom, (s.Y - _pan.Y) / _zoom);

    /// <summary>貼りこむ所が真ん中に来るように表示する。</summary>
    public void FitQuad()
    {
        var c = _w.Corners;
        double l = c.Min(p => p.X), t = c.Min(p => p.Y), r = c.Max(p => p.X), b = c.Max(p => p.Y);
        if (ActualWidth <= 0 || r - l <= 0 || b - t <= 0) return;
        _zoom = Math.Clamp(Math.Min(ActualWidth / ((r - l) * 3), ActualHeight / ((b - t) * 3)), 0.05, 8);
        _pan = new Vector(ActualWidth / 2 - (l + r) / 2 * _zoom, ActualHeight / 2 - (t + b) / 2 * _zoom);
        InvalidateVisual();
    }

    /// <summary>パネル全体を表示する。</summary>
    private void FitPanel()
    {
        if (_w.PanelBitmap is not { } pb || ActualWidth <= 0) { FitQuad(); return; }
        _zoom = Math.Clamp(Math.Min(ActualWidth / (pb.PixelWidth * 1.05), ActualHeight / (pb.PixelHeight * 1.05)), 0.02, 32);
        _pan = new Vector((ActualWidth - pb.PixelWidth * _zoom) / 2, (ActualHeight - pb.PixelHeight * _zoom) / 2);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Back, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var mode = _zoom >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality;
        if (_w.PanelBitmap is { } pb) DrawImage(dc, pb, new Rect(ToScreen(new Point(0, 0)), new Size(pb.PixelWidth * _zoom, pb.PixelHeight * _zoom)), mode, 1);
        if (_w.Warped is { } wb)
            DrawImage(dc, wb, new Rect(ToScreen(_w.WarpedAt), new Size(wb.PixelWidth * _zoom, wb.PixelHeight * _zoom)), mode, _w.PreviewOpacity);
        var pts = _w.Corners.Select(ToScreen).ToArray();
        for (int i = 0; i < 4; i++) dc.DrawLine(QuadPen, pts[i], pts[(i + 1) % 4]);
        for (int i = 0; i < 4; i++) dc.DrawEllipse(i == _selCorner ? Brushes.Orange : Brushes.White, QuadPen, pts[i], 5, 5);
    }

    private static void DrawImage(DrawingContext dc, ImageSource img, Rect r, BitmapScalingMode mode, double opacity)
    {
        var g = new DrawingGroup();
        RenderOptions.SetBitmapScalingMode(g, mode);
        using (var gc = g.Open()) gc.DrawImage(img, r);
        dc.PushOpacity(opacity);
        dc.DrawDrawing(g);
        dc.Pop();
    }

    private int CornerAt(Point s)
    {
        for (int i = 0; i < 4; i++)
            if ((ToScreen(_w.Corners[i]) - s).Length <= 8) return i;
        return -1;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        var s = e.GetPosition(this);
        _last = s;
        // パネルくんと同じく、中・右ドラッグ、またはスペースを押しながら左ドラッグで表示を動かす
        if (e.ChangedButton is MouseButton.Middle or MouseButton.Right || (e.ChangedButton == MouseButton.Left && Keyboard.IsKeyDown(Key.Space)))
        { _panning = true; Cursor = Cursors.ScrollAll; CaptureMouse(); return; }
        if (e.ChangedButton != MouseButton.Left) return;
        _dragCorner = CornerAt(s);
        _selCorner = _dragCorner;
        // 角でなく形の中なら、4 隅をまとめて動かす
        if (_dragCorner < 0)
        {
            if (_w.InsideQuad(ToPanel(s))) _dragMove = true;
            else _panning = true;
        }
        CaptureMouse();
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var s = e.GetPosition(this);
        if (!_panning && _dragCorner < 0 && !_dragMove) Cursor = CursorAt(s);
        if (_panning) { Cursor = Cursors.ScrollAll; _pan += s - _last; InvalidateVisual(); }
        else if (_dragCorner >= 0)
        {
            var pp = ToPanel(s);
            int i = _dragCorner;
            var np = new Point(Math.Round(pp.X), Math.Round(pp.Y));
            if (np != _w.Corners[i]) _w.Edit(() => _w.Corners[i] = np);
        }
        else if (_dragMove)
        {
            var a = ToPanel(_last); var b = ToPanel(s);
            double dx = Math.Round(b.X - a.X), dy = Math.Round(b.Y - a.Y);
            if (dx == 0 && dy == 0) return;
            _w.Edit(() => MoveAll(dx, dy));
            s = ToScreen(new Point(a.X + dx, a.Y + dy));
        }
        _last = s;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        _panning = _dragMove = false;
        _dragCorner = -1;
        Cursor = CursorAt(e.GetPosition(this));
        ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var s = e.GetPosition(this);
        var before = ToPanel(s);
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.2 : 1 / 1.2), 0.02, 64);
        _pan = new Vector(s.X - before.X * _zoom, s.Y - before.Y * _zoom);
        InvalidateVisual();
        e.Handled = true;
    }

    private void ZoomCenter(double zoom)
    {
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var before = ToPanel(c);
        _zoom = Math.Clamp(zoom, 0.02, 64);
        _pan = new Vector(c.X - before.X * _zoom, c.Y - before.Y * _zoom);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // IME がオンでも効くように、ImeProcessed のときは元のキーで見る
        var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (Keyboard.Modifiers == ModifierKeys.Control && key is Key.D0 or Key.NumPad0) { FitPanel(); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && key is Key.D1 or Key.NumPad1) { ZoomCenter(1); e.Handled = true; return; }
        double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
        double dx = key switch { Key.Left => -step, Key.Right => step, _ => 0 };
        double dy = key switch { Key.Up => -step, Key.Down => step, _ => 0 };
        if (dx == 0 && dy == 0) return;
        if (_selCorner >= 0)
        {
            int i = _selCorner;
            _w.Edit(() => _w.Corners[i] = new Point(_w.Corners[i].X + dx, _w.Corners[i].Y + dy));
        }
        else _w.Edit(() => MoveAll(dx, dy));
        e.Handled = true;
    }

    private Cursor? CursorAt(Point s) => CornerAt(s) >= 0 || _w.InsideQuad(ToPanel(s)) ? Cursors.SizeAll : null;

    private void MoveAll(double dx, double dy)
    {
        for (int i = 0; i < 4; i++) _w.Corners[i] = new Point(_w.Corners[i].X + dx, _w.Corners[i].Y + dy);
    }
}
