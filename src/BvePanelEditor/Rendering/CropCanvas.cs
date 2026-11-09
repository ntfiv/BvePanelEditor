using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BvePanelEditor.Core.Crop;
using BvePanelEditor.Core.Psd;

namespace BvePanelEditor.Rendering;

/// <summary>画像ファイルを BGRA で読む。</summary>
public static class ImageLoader
{
    /// <summary>画像を読む（ファイルはロックしない）。Pixels はキャンバス座標 (0,0) 起点。</summary>
    public static (PsdPixels Pixels, BitmapSource Bitmap) Load(string path)
    {
        if (Path.GetExtension(path).Equals(".psd", StringComparison.OrdinalIgnoreCase))
        {
            // PSD は全体を重ねた絵（統合画像）を 1 枚の画像として使う
            using var psd = PsdFile.Open(path);
            var merged = psd.ReadMerged();
            return (merged, ToBitmap(merged));
        }
        var bytes = File.ReadAllBytes(path);
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bi.StreamSource = new MemoryStream(bytes);
        bi.EndInit();
        var conv = new FormatConvertedBitmap(bi, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight;
        var px = new byte[w * h * 4];
        conv.CopyPixels(px, w * 4, 0);
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return (new PsdPixels(new PsdRect(0, 0, w, h), px), bmp);
    }

    /// <summary>読めなければ null。</summary>
    public static PsdPixels? TryLoadPixels(string path)
    {
        try { return File.Exists(path) ? Load(path).Pixels : null; }
        catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    public static BitmapSource ToBitmap(PsdPixels px)
    {
        var bmp = BitmapSource.Create(Math.Max(1, px.Width), Math.Max(1, px.Height), 96, 96, PixelFormats.Bgra32, null,
            px.Bgra.Length > 0 ? px.Bgra : new byte[4], Math.Max(1, px.Width) * 4);
        bmp.Freeze();
        return bmp;
    }
}

/// <summary>切り抜き元の画像を表示し、囲み（CropRegion）を描く・選ぶ・動かす・大きさを変える。</summary>
public sealed class CropCanvas : FrameworkElement
{
    private BitmapSource? _image;
    public BitmapSource? Image { get => _image; set { _image = value; InvalidateVisual(); } }

    public List<CropRegion> Regions { get; set; } = [];
    public HashSet<CropRegion> Selected { get; } = [];

    /// <summary>新しい囲みの名前を決める。</summary>
    public Func<string>? NextName;

    public event Action? SelectionChanged;
    /// <summary>囲みを動かした・大きさを変えた・描いた。</summary>
    public event Action? RegionsChanged;
    public event Action? DeleteRequested;
    public event Action<Point>? PanelMouseMove;

    // ---- 重ねる画像のモード（「重ねる画像」のタブ）: 囲みは描かず、選んだ重ねる画像の枠を描いてドラッグで動かす ----
    private bool _overlayMode;
    /// <summary>重ねる画像のモードか。この間は囲みを描かず、囲みの操作もしない。</summary>
    public bool OverlayMode { get => _overlayMode; set { _overlayMode = value; _mode = Mode.None; InvalidateVisual(); } }
    /// <summary>選んでいる重ねる画像の範囲（パネルの座標）。なければ null。</summary>
    public PsdRect? OverlayRect { get; set; }
    public string OverlayLabel { get; set; } = "";
    /// <summary>ドラッグしている間、範囲に浮かせて描く絵（半透明）。</summary>
    public BitmapSource? OverlayFloating { get; set; }
    /// <summary>重ねる画像の枠の中でドラッグを始めた。</summary>
    public event Action? OverlayDragStarted;
    /// <summary>ドラッグ中（始めからのずれ、px）。</summary>
    public event Action<int, int>? OverlayDragging;
    /// <summary>ドラッグを終えた（始めからのずれ、px）。</summary>
    public event Action<int, int>? OverlayDragFinished;
    /// <summary>矢印キーで動かした（ずれ、px）。</summary>
    public event Action<int, int>? OverlayNudged;
    /// <summary>枠の外をクリックした（その点で重ねる画像を選び直す）。</summary>
    public event Action<Point>? OverlayPicked;
    private int _odx, _ody;

    private double _zoom = 0.4;
    private Vector _offset = new(10, 10);
    public double Zoom => _zoom;

    private enum Mode { None, Pan, Move, Resize, Draw, OverlayMove }
    private Mode _mode;
    private Point _startScreen, _startPanel, _now;
    private Vector _startOffset;
    private int _handle;
    private readonly Dictionary<CropRegion, PsdRect> _startRects = [];
    private bool _moved;

    private static readonly Pen RegionPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x40, 0xFF)), 1.5));
    private static readonly Pen SelPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD8, 0x40)), 2));
    private static readonly Pen DrawPen = Freeze(new Pen(Brushes.White, 1) { DashStyle = DashStyles.Dash });
    private static readonly Brush HandleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xD8, 0x40)));
    private static readonly Brush Back = Freeze(new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2E)));
    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    /// <summary>スペースキーを押しながら左ドラッグで表示を動かす。</summary>
    public SpacePan Space { get; }

    public CropCanvas()
    {
        Space = new SpacePan(this);
        Focusable = true;
        ClipToBounds = true;
    }

    public Point ToPanel(Point s) => new((s.X - _offset.X) / _zoom, (s.Y - _offset.Y) / _zoom);
    public Point ToScreen(Point p) => new(p.X * _zoom + _offset.X, p.Y * _zoom + _offset.Y);
    private Rect ScreenRect(PsdRect r) => new(ToScreen(new Point(r.Left, r.Top)), ToScreen(new Point(r.Right, r.Bottom)));

    public void FitAll()
    {
        if (_image == null || ActualWidth < 10) return;
        _zoom = Math.Min(ActualWidth / _image.PixelWidth, ActualHeight / _image.PixelHeight) * 0.97;
        _offset = new Vector((ActualWidth - _image.PixelWidth * _zoom) / 2, (ActualHeight - _image.PixelHeight * _zoom) / 2);
        InvalidateVisual();
    }

    /// <summary>囲みが画面の中に入るように表示を動かす。</summary>
    public void Reveal(PsdRect r)
    {
        var s = ScreenRect(r);
        if (s.Left >= 0 && s.Top >= 0 && s.Right <= ActualWidth && s.Bottom <= ActualHeight) return;
        _offset += new Vector(ActualWidth / 2 - (s.Left + s.Right) / 2, ActualHeight / 2 - (s.Top + s.Bottom) / 2);
        InvalidateVisual();
    }

    // ---- 描画 ----

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Back, null, new Rect(RenderSize));
        if (_image == null)
        {
            var ft = new FormattedText("上の欄で画像を選んでください", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Yu Gothic UI"), 14, Brushes.Gray, VisualTreeHelper.GetDpi(this).PixelsPerDip)
            { TextAlignment = TextAlignment.Center, MaxTextWidth = Math.Max(100, RenderSize.Width - 40) };
            dc.DrawText(ft, new Point(20, RenderSize.Height / 2));
            return;
        }
        RenderOptions.SetBitmapScalingMode(this, _zoom >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        dc.DrawImage(_image, new Rect(_offset.X, _offset.Y, _image.PixelWidth * _zoom, _image.PixelHeight * _zoom));

        if (_overlayMode)
        {
            DrawOverlayFrame(dc);
            return;
        }
        foreach (var r in Regions)
        {
            bool sel = Selected.Contains(r);
            var sr = ScreenRect(r.Rect);
            dc.DrawRectangle(null, sel ? SelPen : RegionPen, sr);
            if (sel || _zoom >= 0.6) Label(dc, r.Name, new Point(sr.Left, sr.Top - 16), sel ? SelPen.Brush : RegionPen.Brush);
        }
        if (Selected.Count == 1)
            foreach (var h in Handles(Selected.First().Rect)) dc.DrawRectangle(HandleBrush, null, new Rect(h.X - 4, h.Y - 4, 8, 8));
        if (_mode == Mode.Draw && _moved) dc.DrawRectangle(null, DrawPen, new Rect(_startScreen, _now));
    }

    private static readonly Pen OverlayPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD8, 0x40)), 2) { DashStyle = DashStyles.Dash });

    /// <summary>選んでいる重ねる画像の枠（ドラッグ中はずらした所に、絵を半透明で浮かせる）。</summary>
    private void DrawOverlayFrame(DrawingContext dc)
    {
        if (OverlayRect is not { } r) return;
        var moved = _mode == Mode.OverlayMove ? new PsdRect(r.Left + _odx, r.Top + _ody, r.Right + _odx, r.Bottom + _ody) : r;
        var sr = ScreenRect(moved);
        if (_mode == Mode.OverlayMove && OverlayFloating != null)
        {
            dc.PushOpacity(0.65);
            dc.DrawImage(OverlayFloating, new Rect(sr.TopLeft, new Size(OverlayFloating.PixelWidth * _zoom, OverlayFloating.PixelHeight * _zoom)));
            dc.Pop();
        }
        dc.DrawRectangle(null, OverlayPen, sr);
        Label(dc, $"{OverlayLabel}（{moved.Left}, {moved.Top}）", new Point(Math.Max(0, sr.Left), Math.Max(0, sr.Top - 16)), OverlayPen.Brush);
    }

    private void Label(DrawingContext dc, string text, Point at, Brush brush)
    {
        if (string.IsNullOrEmpty(text)) return;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Yu Gothic UI"), 11, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0)), null, new Rect(at, new Size(ft.Width + 4, ft.Height)));
        dc.DrawText(ft, at + new Vector(2, 0));
    }

    /// <summary>大きさを変える 8 つのつまみ（左上から時計回り）。</summary>
    private Point[] Handles(PsdRect r)
    {
        var s = ScreenRect(r);
        double cx = (s.Left + s.Right) / 2, cy = (s.Top + s.Bottom) / 2;
        return [new(s.Left, s.Top), new(cx, s.Top), new(s.Right, s.Top), new(s.Right, cy), new(s.Right, s.Bottom), new(cx, s.Bottom), new(s.Left, s.Bottom), new(s.Left, cy)];
    }

    // ---- マウス ----

    private CropRegion? HitRegion(Point panel)
    {
        // 小さい囲みを優先（大きい囲みの中にある小さい囲みを選べるように）
        return Regions.Where(r => panel.X >= r.Rect.Left && panel.X < r.Rect.Right && panel.Y >= r.Rect.Top && panel.Y < r.Rect.Bottom)
            .OrderBy(r => r.Rect.Width * r.Rect.Height).FirstOrDefault();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (_image == null) return;
        _startScreen = _now = e.GetPosition(this);
        _startPanel = ToPanel(_startScreen);
        _startOffset = _offset;
        _moved = false;
        CaptureMouse();
        if (Space.IsPanButton(e.ChangedButton)) { _mode = Mode.Pan; Space.BeginPan(); return; }
        if (e.ChangedButton != MouseButton.Left) { _mode = Mode.None; ReleaseMouseCapture(); return; }

        if (_overlayMode)
        {
            if (OverlayRect is { } r && _startPanel.X >= r.Left && _startPanel.X < r.Right && _startPanel.Y >= r.Top && _startPanel.Y < r.Bottom)
            {
                _mode = Mode.OverlayMove;
                _odx = _ody = 0;
                OverlayDragStarted?.Invoke();
                return;
            }
            _mode = Mode.None;
            ReleaseMouseCapture();
            OverlayPicked?.Invoke(_startPanel);
            return;
        }

        if (Selected.Count == 1)
        {
            var hs = Handles(Selected.First().Rect);
            for (int i = 0; i < hs.Length; i++)
                if ((hs[i] - _startScreen).Length <= 6) { _mode = Mode.Resize; _handle = i; RememberStart(); return; }
        }
        var hit = HitRegion(_startPanel);
        if (hit != null)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                if (!Selected.Remove(hit)) Selected.Add(hit);
            }
            else if (!Selected.Contains(hit)) { Selected.Clear(); Selected.Add(hit); }
            SelectionChanged?.Invoke();
            _mode = Mode.Move;
            RememberStart();
            InvalidateVisual();
            return;
        }
        _mode = Mode.Draw;
    }

    private void RememberStart()
    {
        _startRects.Clear();
        foreach (var r in Selected) _startRects[r] = r.Rect;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var sp = e.GetPosition(this);
        var pp = ToPanel(sp);
        PanelMouseMove?.Invoke(pp);
        if (_mode == Mode.None) return;
        _now = sp;
        if ((sp - _startScreen).Length > 2) _moved = true;
        if (!_moved) return;
        var d = pp - _startPanel;
        int dx = (int)Math.Round(d.X), dy = (int)Math.Round(d.Y);
        switch (_mode)
        {
            case Mode.Pan:
                _offset = _startOffset + (sp - _startScreen);
                break;
            case Mode.OverlayMove:
                _odx = dx; _ody = dy;
                OverlayDragging?.Invoke(dx, dy);
                break;
            case Mode.Move:
                foreach (var (r, s) in _startRects) r.Rect = new PsdRect(s.Left + dx, s.Top + dy, s.Right + dx, s.Bottom + dy);
                break;
            case Mode.Resize:
                foreach (var (r, s) in _startRects)
                {
                    int l = s.Left, t = s.Top, rr = s.Right, b = s.Bottom;
                    if (_handle is 0 or 6 or 7) l = Math.Min(s.Left + dx, rr - 1);
                    if (_handle is 2 or 3 or 4) rr = Math.Max(s.Right + dx, l + 1);
                    if (_handle is 0 or 1 or 2) t = Math.Min(s.Top + dy, b - 1);
                    if (_handle is 4 or 5 or 6) b = Math.Max(s.Bottom + dy, t + 1);
                    r.Rect = new PsdRect(l, t, rr, b);
                }
                break;
        }
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_mode == Mode.None) return;
        var mode = _mode;
        _mode = Mode.None;
        ReleaseMouseCapture();
        Space.EndPan();
        switch (mode)
        {
            case Mode.Draw:
                if (_moved)
                {
                    var a = ToPanel(_startScreen);
                    var b = ToPanel(_now);
                    var r = new PsdRect((int)Math.Floor(Math.Min(a.X, b.X)), (int)Math.Floor(Math.Min(a.Y, b.Y)),
                        (int)Math.Ceiling(Math.Max(a.X, b.X)), (int)Math.Ceiling(Math.Max(a.Y, b.Y)));
                    if (r.Width >= 2 && r.Height >= 2)
                    {
                        var reg = new CropRegion { Rect = r, Name = NextName?.Invoke() ?? "" };
                        Regions.Add(reg);
                        Selected.Clear();
                        Selected.Add(reg);
                        RegionsChanged?.Invoke();
                        SelectionChanged?.Invoke();
                    }
                }
                else if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && Selected.Count > 0)
                {
                    Selected.Clear();
                    SelectionChanged?.Invoke();
                }
                break;
            case Mode.Move:
            case Mode.Resize:
                if (_moved) RegionsChanged?.Invoke();
                break;
            case Mode.OverlayMove:
                OverlayDragFinished?.Invoke(_moved ? _odx : 0, _moved ? _ody : 0);
                _odx = _ody = 0;
                break;
        }
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        Space.EndPan();
        if (_mode is Mode.Move or Mode.Resize && _moved) { _mode = Mode.None; RegionsChanged?.Invoke(); }
        if (_mode == Mode.OverlayMove) { _mode = Mode.None; OverlayDragFinished?.Invoke(_moved ? _odx : 0, _moved ? _ody : 0); _odx = _ody = 0; }
        _mode = Mode.None;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var sp = e.GetPosition(this);
        var p = ToPanel(sp);
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.2 : 1 / 1.2), 0.02, 32);
        _offset = new Vector(sp.X - p.X * _zoom, sp.Y - p.Y * _zoom);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var d = e.Key switch { Key.Left => (-1, 0), Key.Right => (1, 0), Key.Up => (0, -1), Key.Down => (0, 1), _ => (0, 0) };
        if (_overlayMode)
        {
            if (d == (0, 0) || OverlayRect == null) return;
            int kk = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
            OverlayNudged?.Invoke(d.Item1 * kk, d.Item2 * kk);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete) { DeleteRequested?.Invoke(); e.Handled = true; return; }
        if (d == (0, 0) || Selected.Count == 0) return;
        int k = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        foreach (var r in Selected) r.Rect = new PsdRect(r.Rect.Left + d.Item1 * k, r.Rect.Top + d.Item2 * k, r.Rect.Right + d.Item1 * k, r.Rect.Bottom + d.Item2 * k);
        RegionsChanged?.Invoke();
        InvalidateVisual();
        e.Handled = true;
    }
}
