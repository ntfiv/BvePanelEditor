using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BvePanelEditor.Core;

namespace BvePanelEditor.Rendering;

/// <summary>パネルの表示と、マウスでの配置編集。</summary>
public sealed class PanelCanvas : FrameworkElement
{
    public PanelScene? Scene { get; private set; }
    public PanelDocument? Document { get; private set; }

    /// <summary>状態量（Subjects.BaseKey → 値）。</summary>
    public Dictionary<string, double> State { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool AllLampsOn { get; set; } = true;
    public double NightAmount { get; set; }
    public bool ShowViewFrame { get; set; } = true;

    /// <summary>同時に描く画面枠（ラベル "16:9" と、横÷縦）。</summary>
    public List<(string Label, double Aspect)> FrameAspects { get; set; } = [("16:9", 16.0 / 9), ("16:10", 16.0 / 10), ("4:3", 4.0 / 3)];

    /// <summary>描いている画面枠のうち、いちばん縦に長いものの横÷縦（なければ 16:9）。</summary>
    public double TallestFrameAspect => FrameAspects.Count > 0 ? FrameAspects.Min(f => f.Aspect) : 16.0 / 9;
    /// <summary>揺れの余白（画面幅に対する %）。0 なら描かない。</summary>
    public double ShakeMarginPercent { get; set; } = 3;

    private bool _positionLocked = true;
    /// <summary>位置ロック。true の間はマウスや矢印キーで位置・半径・中心を動かさない（選ぶ・表示を動かすのはできる）。</summary>
    public bool PositionLocked
    {
        get => _positionLocked;
        set { _positionLocked = value; InvalidateVisual(); }
    }

    /// <summary>ロック中に動かそうとした。</summary>
    public event Action? LockedEditAttempt;

    /// <summary>Ctrl＋ドラッグで計器の値を動かし始めた（引数はセクション番号）。</summary>
    public event Action<int>? ValueDragStarted;
    /// <summary>Ctrl＋ドラッグ中（セクション番号、押した所からの縦の移動量 px。上が正、細かく動かすとき true）。</summary>
    public event Action<int, double, bool>? ValueDragged;
    /// <summary>Ctrl＋クリック（動かさずに離した）。</summary>
    public event Action<int>? ValueClicked;
    /// <summary>主に選んでいる計器（プロパティ欄に出すもの）。</summary>
    public int Selected { get; private set; } = -1;

    private readonly List<int> _set = [];
    /// <summary>選んでいる計器すべて（ドラッグ・矢印キーで一緒に動く）。</summary>
    public IReadOnlyList<int> SelectedSet => _set;
    /// <summary>グループごと選んでいるときのグループ名。</summary>
    public string? SelectedGroup { get; private set; }
    /// <summary>ダブルクリックで「中に入った」グループ。この中の計器はクリックで 1 つずつ選べる。</summary>
    public string? EnteredGroup { get; private set; }

    /// <summary>キャンバスの操作で選択が変わった（引数は主に選んでいる計器）。</summary>
    public event Action<int>? SelectionChanged;
    /// <summary>ドラッグや矢印キーで値を変え終わった（引数はセクション番号）。</summary>
    public event Action<int>? EditCommitted;
    public event Action<Point>? PanelMouseMove;
    public event Action? ViewChanged;
    /// <summary>毎フレーム（引数は経過秒）。スイープ表示に使う。</summary>
    public event Action<double>? Ticked;

    private double _zoom = 1;
    private Vector _offset = new(20, 20);
    public double Zoom => _zoom;

    private enum DragMode { None, Move, Pan, Radius, Center, Origin, Blocked, Value, Marquee }
    private Point _marqueeEnd;                 // 囲み選択の今の位置（画面座標）
    private List<int> _marqueeBase = [];       // Shift で足すときの元の選択
    private DragMode _drag;
    private Point _dragStartScreen;
    private Point _dragStartPanel;
    private Point _dragStartValue;
    private Point _dragStartOther;
    private Vector _dragStartOffset;
    private bool _dragMoved;
    private int _hover = -1;
    private int _valueSection = -1;
    private TimeSpan _lastFrame;

    private static readonly Brush Background = Freeze(new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2E)));
    private static readonly Pen SelPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD8, 0x40)), 1.5));
    private static readonly Pen HoverPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), 1) { DashStyle = DashStyles.Dash });
    /// <summary>表示範囲の外を暗くする（30%）。</summary>
    private static readonly Brush OutsideLimitBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x4D, 0, 0, 0)));
    private static readonly Brush LimitLabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)));
    private static readonly Pen LimitPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xA0, 0xA0, 0xA0)), 1) { DashStyle = DashStyles.Dash });
    private static readonly Pen BleedPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0x5A, 0xA0)), 1) { DashStyle = new DashStyle([2, 3], 0) });

    // 画面枠は比率ごとに色を変え、少し薄く描く
    private static readonly Dictionary<string, (Pen Pen, Brush Label)> FrameStyles = new()
    {
        ["16:9"] = MakeFrameStyle(0x40, 0xD0, 0xFF),
        ["16:10"] = MakeFrameStyle(0x70, 0xE0, 0x70),
        ["4:3"] = MakeFrameStyle(0xFF, 0xB0, 0x40),
    };
    private static readonly (Pen Pen, Brush Label) OtherFrameStyle = MakeFrameStyle(0xC0, 0xA0, 0xFF);

    private static (Pen, Brush) MakeFrameStyle(byte r, byte g, byte b)
    {
        var pen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x90, r, g, b)), 1) { DashStyle = DashStyles.DashDot });
        var label = new SolidColorBrush(Color.FromArgb(0xC0, r, g, b));
        label.Freeze();
        return (pen, label);
    }

    private static (Pen Pen, Brush Label) FrameStyle(string label) => FrameStyles.TryGetValue(label, out var s) ? s : OtherFrameStyle;
    // 中心・消失点はふだんは少し薄く、選んだもの（矢印キーで動かすもの）は濃く太く描く
    private static readonly Pen CenterPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xA0, 0x40, 0xD0, 0xFF)), 1.2));
    private static readonly Pen OriginPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0x90, 0x30)), 1.2));
    private static readonly Pen AlignedPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0x80, 0xFF, 0x80)), 1.5));
    private static readonly Pen CenterActivePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x40, 0xD0, 0xFF)), 2.5));
    private static readonly Pen OriginActivePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x90, 0x30)), 2.5));
    private static readonly Pen MissingPen = Freeze(new Pen(Brushes.Red, 1) { DashStyle = DashStyles.Dash });
    private static readonly Pen GroupPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0x3C)), 1.5) { DashStyle = DashStyles.Dash });
    private static readonly Pen GroupInsidePen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0x9A, 0x3C)), 1) { DashStyle = DashStyles.Dot });
    private static readonly Brush HandleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xD8, 0x40)));

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public PanelCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Loaded += (_, _) => CompositionTarget.Rendering += OnFrame;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
    }

    public void SetScene(PanelDocument doc, PanelScene scene)
    {
        Document = doc;
        Scene = scene;
        if (Selected >= doc.Sections.Count) Selected = -1;
        _set.RemoveAll(i => i >= doc.Sections.Count);
        if (SelectedGroup != null && !doc.Sections.Any(s => s.Group == SelectedGroup)) SelectedGroup = null;
        if (EnteredGroup != null && !doc.Sections.Any(s => s.Group == EnteredGroup)) EnteredGroup = null;
        InvalidateVisual();
    }

    public void Clear()
    {
        Document = null;
        Scene = null;
        Selected = -1;
        _set.Clear();
        SelectedGroup = EnteredGroup = null;
        InvalidateVisual();
    }

    /// <summary>パネル本体のどの点を選んでいるか（矢印キーで動かす）。Shift＋クリックで両方選べる。</summary>
    [Flags]
    public enum PanelPoint { None = 0, Center = 1, Origin = 2, Both = Center | Origin }

    private PanelPoint _activePoint;
    /// <summary>選んでいる中心・消失点（パネル本体を選んでいるときだけ）。</summary>
    public PanelPoint ActivePoint
    {
        get => _activePoint;
        set { if (_activePoint == value) return; _activePoint = value; InvalidateVisual(); ActivePointChanged?.Invoke(value); }
    }

    /// <summary>選んでいる中心・消失点を d だけ動かす（矢印キー）。動かしたら true。</summary>
    public bool MoveActivePoint(Vector d)
    {
        if (Scene == null || Document == null || Selected < 0 || Selected != Scene.ThisSection || ActivePoint == PanelPoint.None) return false;
        bool moved = false;
        if (ActivePoint.HasFlag(PanelPoint.Center) && Scene.HasCenter) { ApplyPoint(DragMode.Center, Scene.Center + d); moved = true; }
        if (ActivePoint.HasFlag(PanelPoint.Origin) && Scene.HasOrigin) { ApplyPoint(DragMode.Origin, Scene.Origin + d); moved = true; }
        if (!moved) return false;
        EditCommitted?.Invoke(Selected);
        return true;
    }

    /// <summary>
    /// 中心と消失点の横の位置（X）をそろえる。originToCenter なら消失点を中心の X に、そうでなければ中心を消失点の X に合わせる。
    /// 位置ロック中は動かさない（false）。
    /// </summary>
    public bool AlignPointsX(bool originToCenter)
    {
        if (Scene == null || Document == null || Selected < 0 || Selected != Scene.ThisSection || !Scene.HasCenter || !Scene.HasOrigin) return false;
        if (PositionLocked) { LockedEditAttempt?.Invoke(); return false; }
        if (originToCenter) ApplyPoint(DragMode.Origin, new Point(Scene.Center.X, Scene.Origin.Y));
        else ApplyPoint(DragMode.Center, new Point(Scene.Origin.X, Scene.Center.Y));
        EditCommitted?.Invoke(Selected);
        return true;
    }

    /// <summary>中心・消失点を選んだ・外した。</summary>
    public event Action<PanelPoint>? ActivePointChanged;

    private readonly HashSet<int> _fresh = [];

    /// <summary>
    /// 追加したばかりの計器（画像のドロップ・計器を追加・指針を置く）。それだけを選んでいる間は、位置ロック中でも動かせる。
    /// ほかを選ぶ・選択を外すと空になる（ロックに戻る）。
    /// </summary>
    public void AllowFreshEdit(IEnumerable<int> sections)
    {
        _fresh.Clear();
        _fresh.UnionWith(sections);
        InvalidateVisual();
        FreshChanged?.Invoke();
    }

    public void ClearFreshEdit()
    {
        if (_fresh.Count == 0) return;
        _fresh.Clear();
        InvalidateVisual();
        FreshChanged?.Invoke();
    }

    /// <summary>追加したばかりで動かせる計器があるか。</summary>
    public bool HasFreshEdit => _fresh.Count > 0;

    /// <summary>追加したばかりの計器の「動かしてよい」が付いた・外れた。</summary>
    public event Action? FreshChanged;

    /// <summary>今選んでいる計器を、位置ロックのために動かせないか（追加したばかりの計器だけを選んでいるなら動かせる）。</summary>
    public bool EditLocked => PositionLocked && !(_fresh.Count > 0 && _set.Count > 0 && _set.All(_fresh.Contains));

    /// <summary>1 つだけ選ぶ。</summary>
    public void Select(int section, bool raise = false)
    {
        var g = GroupOf(section);
        SetSelection(section, section >= 0 ? [section] : [], null, g != null && g == EnteredGroup ? g : null);
        if (raise) SelectionChanged?.Invoke(section);
    }

    /// <summary>選択をまとめて設定する（イベントは出さない）。</summary>
    public void SetSelection(int primary, IEnumerable<int> set, string? group, string? entered = null)
    {
        var list = set.ToList();
        if (primary >= 0 && !list.Contains(primary)) list.Add(primary);
        if (_fresh.Count > 0 && (list.Count == 0 || !list.All(_fresh.Contains)))
        {
            _fresh.Clear();
            FreshChanged?.Invoke();
        }
        set = list;
        if (Scene == null || primary != Scene.ThisSection) ActivePoint = PanelPoint.None;
        Selected = primary;
        _set.Clear();
        _set.AddRange(set.Distinct());
        if (primary >= 0 && !_set.Contains(primary)) _set.Insert(0, primary);
        SelectedGroup = group;
        EnteredGroup = entered;
        InvalidateVisual();
    }

    private string? GroupOf(int section) =>
        Document != null && section >= 0 && section < Document.Sections.Count ? Document.Sections[section].Group : null;

    private List<int> MembersOf(string group) => Document?.Members(group) ?? [];

    /// <summary>
    /// パネル上のクリックで選ぶ。グループの計器なら最初はグループ全体、ダブルクリックでその 1 つ（グループの中に入る）。
    /// Shift で選択に足す・外す。戻り値は当たった計器（なければ -1）。
    /// </summary>
    public int SelectAt(Point panel, bool doubleClick, bool shift)
    {
        if (Scene == null || Document == null) return -1;
        int hit = HitItem(panel);
        if (hit < 0)
        {
            if (!shift) { SetSelection(Scene.ThisSection, Scene.ThisSection >= 0 ? [Scene.ThisSection] : [], null); SelectionChanged?.Invoke(Selected); }
            return -1;
        }
        var g = GroupOf(hit);
        if (shift)
        {
            var unit = g != null && g != EnteredGroup ? MembersOf(g) : [hit];
            var set = _set.ToList();
            if (unit.All(set.Contains)) set.RemoveAll(unit.Contains); else set.AddRange(unit.Where(i => !set.Contains(i)));
            set.Sort();
            int primary = set.Contains(hit) ? hit : set.Count > 0 ? set[0] : -1;
            string? grp = set.Count > 0 && set.Select(GroupOf).Distinct().Count() == 1 && GroupOf(set[0]) is { } only
                && MembersOf(only).Count == set.Count ? only : null;
            SetSelection(primary, set, grp, null);
        }
        else if (doubleClick && g != null)
            SetSelection(hit, [hit], null, g);                 // グループの中に入って 1 つだけ
        else if (g != null && g == EnteredGroup)
            SetSelection(hit, [hit], null, g);                 // 中に入ったまま別の計器
        else if (_set.Count > 1 && _set.Contains(hit))
            SetSelection(hit, _set.ToList(), SelectedGroup, EnteredGroup); // 選んでいる全部をそのまま動かす
        else if (g != null)
            SetSelection(hit, MembersOf(g), g, null);         // グループ全体
        else
            SetSelection(hit, [hit], null, null);
        SelectionChanged?.Invoke(Selected);
        return hit;
    }

    // ---- 表示位置 ----

    private Matrix View
    {
        get
        {
            var m = Matrix.Identity;
            m.Scale(_zoom, _zoom);
            m.Translate(_offset.X, _offset.Y);
            return m;
        }
    }

    public Point ToPanel(Point screen) => new((screen.X - _offset.X) / _zoom, (screen.Y - _offset.Y) / _zoom);
    public Point ToScreen(Point panel) => new(panel.X * _zoom + _offset.X, panel.Y * _zoom + _offset.Y);

    public void FitAll()
    {
        if (Scene == null || ActualWidth < 10 || ActualHeight < 10) return;
        var size = Scene.ContentSize;
        double z = Math.Min(ActualWidth / size.Width, ActualHeight / size.Height) * 0.95;
        SetZoom(z);
        _offset = new Vector((ActualWidth - size.Width * _zoom) / 2, (ActualHeight - size.Height * _zoom) / 2);
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    public void ZoomAt(double zoom, Point screenAnchor)
    {
        var p = ToPanel(screenAnchor);
        SetZoom(zoom);
        _offset = new Vector(screenAnchor.X - p.X * _zoom, screenAnchor.Y - p.Y * _zoom);
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    public void ZoomCenter(double zoom) => ZoomAt(zoom, new Point(ActualWidth / 2, ActualHeight / 2));

    /// <summary>指定のパネル座標を画面の中央に持ってくる。</summary>
    public void CenterOn(Point panel)
    {
        _offset = new Vector(ActualWidth / 2 - panel.X * _zoom, ActualHeight / 2 - panel.Y * _zoom);
        InvalidateVisual();
    }

    public Point ViewCenterPanel => ToPanel(new Point(ActualWidth / 2, ActualHeight / 2));

    private void SetZoom(double z)
    {
        _zoom = Math.Clamp(z, 0.02, 32);
        RenderOptions.SetBitmapScalingMode(this, _zoom >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }

    // ---- 描画 ----

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Background, null, new Rect(RenderSize));
        if (Scene == null)
        {
            var ft = new FormattedText("ファイル → 開く でパネルファイルか車両ファイルを開いてください（ドラッグ＆ドロップでも開けます）",
                CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Yu Gothic UI"), 14, Brushes.Gray,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            // 左上は位置ロックのボタンと重なるので、画面の中央に置く（狭いときは折り返す）
            ft.TextAlignment = TextAlignment.Center;
            ft.MaxTextWidth = Math.Max(100, RenderSize.Width - 40);
            dc.DrawText(ft, new Point(20, Math.Max(20, (RenderSize.Height - ft.Height) / 2)));
            return;
        }

        dc.PushTransform(new MatrixTransform(View));
        if (!Scene.ThisHidden) DrawDayNight(dc, Scene.BaseDay, Scene.BaseNight, new Point(0, 0), Scene.ThisSection >= 0);
        // 一覧で非表示にした計器は描かない
        foreach (var item in Scene.Items) if (!item.Hidden) DrawItem(dc, item);
        dc.Pop();

        DrawOverlays(dc);
        if (_drag == DragMode.Marquee && _dragMoved)
            dc.DrawRectangle(MarqueeFill, MarqueePen, new Rect(_dragStartScreen, _marqueeEnd));
    }

    private static readonly Brush MarqueeFill = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0x60, 0xB0, 0xFF)));
    private static readonly Pen MarqueePen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xE0, 0xA0, 0xD0, 0xFF)), 1) { DashStyle = DashStyles.Dash });

    /// <summary>昼画像と夜画像を、それぞれの元の大きさで左上をそろえて描く（夜画像は NightAmount の濃さで重ねる）。</summary>
    private void DrawDayNight(DrawingContext dc, BitmapSource? day, BitmapSource? night, Point topLeft, bool markMissing)
    {
        if (day != null) dc.DrawImage(day, new Rect(topLeft, new Size(day.PixelWidth, day.PixelHeight)));
        if (night != null && NightAmount > 0)
        {
            dc.PushOpacity(NightAmount);
            dc.DrawImage(night, new Rect(topLeft, new Size(night.PixelWidth, night.PixelHeight)));
            dc.Pop();
        }
        if (day == null && markMissing)
        {
            var rect = new Rect(topLeft, night != null ? new Size(night.PixelWidth, night.PixelHeight) : new Size(32, 32));
            var pen = new Pen(Brushes.Red, 1 / _zoom) { DashStyle = DashStyles.Dash };
            dc.DrawRectangle(null, pen, rect);
            dc.DrawLine(pen, rect.TopLeft, rect.BottomRight);
            dc.DrawLine(pen, rect.TopRight, rect.BottomLeft);
        }
    }

    private double ValueOf(RenderItem item) => Subjects.Resolve(item.Subject, State);

    private void DrawItem(DrawingContext dc, RenderItem item)
    {
        double v = ValueOf(item);
        switch (item.Kind)
        {
            case SectionKind.PilotLamp:
                if (AllLampsOn || Math.Abs(v) > 1e-9)
                    DrawDayNight(dc, item.Day, item.Night, item.Location, true);
                break;

            case SectionKind.DigitalNumber:
                {
                    double w = item.ImageWidth, h = item.ImageHeight;
                    if (item.Interval <= 0)
                    {
                        DrawDayNight(dc, item.Day, item.Night, item.Location, true);
                        break;
                    }
                    int frame = Math.Clamp((int)Math.Floor(v), 0, item.Frames - 1);
                    var clip = new Rect(item.Location, new Size(w, item.Interval));
                    dc.PushClip(new RectangleGeometry(clip));
                    DrawDayNight(dc, item.Day, item.Night, new Point(item.Location.X, item.Location.Y - frame * item.Interval), true);
                    dc.Pop();
                    break;
                }

            case SectionKind.Needle:
                {
                    double angle = item.AngleReady ? item.Angle : item.TargetAngle(v);
                    if (item.Tilt != null && (item.Day != null || item.Night != null))
                    {
                        DrawTiltedNeedle(dc, item, item.NeedleMatrix(angle));
                        break;
                    }
                    dc.PushTransform(new MatrixTransform(item.NeedleMatrix(angle)));
                    DrawDayNight(dc, item.Day, item.Night, new Point(0, 0), true);
                    dc.Pop();
                    break;
                }

            case SectionKind.DigitalGauge:
                {
                    // BVE の説明では Color は「LED 消灯の色」。点いている所（最小〜今の値）はパネルの絵が見え、
                    // 消えている所（今の値〜最大）をこの色で覆う
                    double val = v;
                    if (item.Step > 0) val = item.Minimum + Math.Floor((v - item.Minimum) / item.Step + 1e-9) * item.Step;
                    var geo = item.Tilt != null ? TiltedPie(item, item.TargetAngle(val), item.LastAngle) : Pie(item.Location, item.Radius, item.TargetAngle(val), item.LastAngle);
                    if (geo != null) dc.DrawGeometry(new SolidColorBrush(item.GaugeColor), null, geo);
                    break;
                }
        }
    }

    /// <summary>
    /// Tilt のある針を描く。傾けた絵は平行四辺形にならない（遠近がつく）ので、針の画像を格子の三角形に分け、
    /// 三角形ごとに、その形へ切り抜いてアフィン変換で描く。
    /// </summary>
    private void DrawTiltedNeedle(DrawingContext dc, RenderItem item, Matrix m)
    {
        double w = item.ImageWidth, h = item.ImageHeight;
        int nx = Math.Clamp((int)Math.Ceiling(w / 16), 1, 8), ny = Math.Clamp((int)Math.Ceiling(h / 16), 1, 8);
        var src = new Point[nx + 1, ny + 1];
        var dst = new Point[nx + 1, ny + 1];
        for (int j = 0; j <= ny; j++)
            for (int i = 0; i <= nx; i++)
            {
                src[i, j] = new Point(w * i / nx, h * j / ny);
                dst[i, j] = item.Project(m.Transform(src[i, j]));
            }
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                DrawTriangle(dc, item, src[i, j], src[i + 1, j], src[i + 1, j + 1], dst[i, j], dst[i + 1, j], dst[i + 1, j + 1]);
                DrawTriangle(dc, item, src[i, j], src[i + 1, j + 1], src[i, j + 1], dst[i, j], dst[i + 1, j + 1], dst[i, j + 1]);
            }
    }

    /// <summary>画像の三角形 s0-s1-s2 を、パネルの三角形 d0-d1-d2 へ写して描く（継ぎ目が出ないよう、切り抜きは少し広げる）。</summary>
    private void DrawTriangle(DrawingContext dc, RenderItem item, Point s0, Point s1, Point s2, Point d0, Point d1, Point d2)
    {
        var from = new Matrix(s1.X - s0.X, s1.Y - s0.Y, s2.X - s0.X, s2.Y - s0.Y, s0.X, s0.Y);
        var to = new Matrix(d1.X - d0.X, d1.Y - d0.Y, d2.X - d0.X, d2.Y - d0.Y, d0.X, d0.Y);
        if (!from.HasInverse || Math.Abs(to.Determinant) < 1e-9) return;
        from.Invert();
        var c = new Point((d0.X + d1.X + d2.X) / 3, (d0.Y + d1.Y + d2.Y) / 3);
        double grow = 0.6 / Math.Max(_zoom, 1e-6);
        Point Grow(Point p)
        {
            var v = p - c;
            double len = v.Length;
            return len < 1e-9 ? p : p + v * (grow / len);
        }
        var clip = new StreamGeometry();
        using (var g = clip.Open())
        {
            g.BeginFigure(Grow(d0), true, true);
            g.PolyLineTo([Grow(d1), Grow(d2)], false, false);
        }
        clip.Freeze();
        dc.PushClip(clip);
        dc.PushTransform(new MatrixTransform(from * to));
        DrawDayNight(dc, item.Day, item.Night, new Point(0, 0), false);
        dc.Pop();
        dc.Pop();
    }

    /// <summary>Tilt のあるデジタルゲージの扇形（円弧を点に分けて、傾けた多角形にする）。</summary>
    private static Geometry? TiltedPie(RenderItem item, double a0, double a1)
    {
        double r = item.Radius;
        if (r <= 0 || Math.Abs(a1 - a0) < 0.01) return null;
        bool full = Math.Abs(a1 - a0) >= 359.99;
        int n = Math.Max(2, (int)Math.Ceiling(Math.Abs(a1 - a0) / 2));
        var pts = new List<Point>();
        for (int k = 0; k <= n; k++) pts.Add(item.Project(OnCircle(item.Location, r, a0 + (a1 - a0) * k / n)));
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(full ? pts[0] : item.Location, true, true);
            g.PolyLineTo(full ? pts.Skip(1).ToList() : pts, true, true);
        }
        geo.Freeze();
        return geo;
    }

    /// <summary>計器の面の円（半径 r）を、Tilt で傾けた形で描く（画面座標）。</summary>
    private void TiltedCircle(DrawingContext dc, RenderItem item, double r, Pen pen)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(ToScreen(item.Project(OnCircle(item.Location, r, 0))), false, true);
            g.PolyLineTo(Enumerable.Range(1, 71).Select(k => ToScreen(item.Project(OnCircle(item.Location, r, k * 5)))).ToList(), true, true);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    private static Point OnCircle(Point c, double r, double deg)
    {
        double a = deg * Math.PI / 180;
        return new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
    }

    private static Geometry? Pie(Point c, double r, double a0, double a1)
    {
        if (r <= 0 || Math.Abs(a1 - a0) < 0.01) return null;
        if (Math.Abs(a1 - a0) >= 359.99) return new EllipseGeometry(c, r, r);
        var fig = new PathFigure { StartPoint = c, IsClosed = true, IsFilled = true };
        fig.Segments.Add(new LineSegment(OnCircle(c, r, a0), true));
        fig.Segments.Add(new ArcSegment(OnCircle(c, r, a1), new Size(r, r), 0, Math.Abs(a1 - a0) > 180,
            a1 > a0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true));
        var g = new PathGeometry();
        g.Figures.Add(fig);
        return g;
    }

    private void DrawOverlays(DrawingContext dc)
    {
        var scene = Scene!;
        bool thisSelected = Selected >= 0 && Selected == scene.ThisSection;
        // 表示範囲・画面枠はメニューのチェックだけで出し入れする（パネル本体の [This] を選んでいても、外したら描かない）
        if (ShowViewFrame)
        {
            if (!scene.ViewLimit.IsEmpty)
            {
                var lr = new Rect(ToScreen(scene.ViewLimit.TopLeft), ToScreen(scene.ViewLimit.BottomRight));
                // 表示範囲の外を暗くする
                var outside = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(RenderSize)), new RectangleGeometry(lr));
                dc.DrawGeometry(OutsideLimitBrush, null, outside);
                dc.DrawRectangle(null, LimitPen, lr);
                Label(dc, "表示範囲", new Point(lr.Left + 4, lr.Bottom - 18), LimitLabelBrush);
            }
            if (scene.HasCenter && scene.Resolution > 0 && FrameAspects.Count > 0)
            {
                // 縦に長い（比率の小さい）枠から順に描く。ラベルは重ならないよう横にずらす
                var frames = FrameAspects.OrderBy(f => f.Aspect).ToList();
                double m = scene.Resolution * ShakeMarginPercent / 100;
                for (int i = 0; i < frames.Count; i++)
                {
                    var (label, aspect) = frames[i];
                    double w = scene.Resolution, h = scene.Resolution / Math.Max(0.1, aspect);
                    var r = new Rect(scene.Center.X - w / 2, scene.Center.Y - h / 2, w, h);
                    var (pen, brush) = FrameStyle(label);
                    // 揺れの余白: 揺れたときに画面に入ってくるかもしれない範囲（印刷の塗り足しと同じ考え方）
                    if (ShakeMarginPercent > 0)
                    {
                        var b = new Rect(r.X - m, r.Y - m, r.Width + 2 * m, r.Height + 2 * m);
                        var sb = new Rect(ToScreen(b.TopLeft), ToScreen(b.BottomRight));
                        dc.DrawRectangle(null, BleedPen, sb);
                        // 揺れの余白のラベルは、いちばん縦に長い枠の分だけ
                        if (i == 0)
                            Label(dc, $"揺れの余白（目安） {PanelValues.Num(ShakeMarginPercent)}%・{PanelValues.Num(Math.Round(m))}px", new Point(sb.Left + 4, sb.Bottom - 18), BleedPen.Brush);
                    }
                    dc.DrawRectangle(null, pen, new Rect(ToScreen(r.TopLeft), ToScreen(r.BottomRight)));
                    Label(dc, i == 0 ? $"画面 {label}（ズーム 1 倍）" : $"画面 {label}", ToScreen(r.TopLeft) + new Vector(4 + i * 130, 2), brush);
                }
            }
        }
        // 中心・消失点の印は、枠を出しているときと、[This] を選んで動かすとき
        if (ShowViewFrame || thisSelected)
        {
            bool cOn = thisSelected && ActivePoint.HasFlag(PanelPoint.Center), oOn = thisSelected && ActivePoint.HasFlag(PanelPoint.Origin);
            if (scene.HasCenter) DrawPoint(dc, ToScreen(scene.Center), cOn ? CenterActivePen : CenterPen, cOn, diagonal: false);
            if (scene.HasOrigin) DrawPoint(dc, ToScreen(scene.Origin), oOn ? OriginActivePen : OriginPen, oOn, diagonal: true);
        }
        if (thisSelected)
        {
            string Hint(bool on, Point p) => on ? $"（{PanelValues.Num(p.X)}, {PanelValues.Num(p.Y)}）矢印キーで 1px・Shift で 10px" : "";
            bool cOn = ActivePoint.HasFlag(PanelPoint.Center), oOn = ActivePoint.HasFlag(PanelPoint.Origin);
            // 両方選んでいて横の位置がそろっていれば、縦の線で結んで知らせる
            if (cOn && oOn && scene.HasCenter && scene.HasOrigin)
            {
                var a = ToScreen(scene.Center); var b = ToScreen(scene.Origin);
                var pen = Math.Abs(scene.Center.X - scene.Origin.X) < 0.5 ? AlignedPen : LimitPen;
                dc.DrawLine(pen, a, b);
                if (pen == AlignedPen) Label(dc, "横の位置がそろっています", new Point(a.X - 150, (a.Y + b.Y) / 2 - 8), AlignedPen.Brush);
            }
            // ラベルが重ならないよう、上にある点は上に、下にある点は下に出す（ふだんは中心が下、消失点が上）
            bool centerLower = !(scene.HasCenter && scene.HasOrigin) || scene.Center.Y >= scene.Origin.Y;
            Vector Off(bool on, bool lower) => new(on ? 20 : 8, lower ? (on ? 10 : 4) : (on ? -26 : -18));
            if (scene.HasCenter) Label(dc, "中心 Center" + Hint(cOn, scene.Center), ToScreen(scene.Center) + Off(cOn, centerLower), (cOn ? CenterActivePen : CenterPen).Brush);
            if (scene.HasOrigin) Label(dc, "消失点 Origin" + Hint(oOn, scene.Origin), ToScreen(scene.Origin) + Off(oOn, !centerLower), (oOn ? OriginActivePen : OriginPen).Brush);
        }

        // マウスを乗せた計器（グループならグループの枠）
        if (_hover >= 0 && !_set.Contains(_hover) && scene.ItemFor(_hover) is { } hv)
        {
            var hg = GroupOf(_hover);
            if (hg != null && hg != EnteredGroup) GroupBox(dc, hg, HoverPen, Brushes.White, $"グループ: {hg}");
            else
            {
                Poly(dc, hv.Outline(), HoverPen);
                TitleLabel(dc, hv, Brushes.White);
            }
        }

        // グループごとの選択・中に入ったグループの枠
        if (SelectedGroup != null) GroupBox(dc, SelectedGroup, GroupPen, GroupPen.Brush, $"グループ: {SelectedGroup}（{MembersOf(SelectedGroup).Count}）");
        else if (EnteredGroup != null) GroupBox(dc, EnteredGroup, GroupInsidePen, GroupPen.Brush, $"グループ: {EnteredGroup} の中");

        foreach (var idx in _set)
            if (idx != Selected && scene.ItemFor(idx) is { } other) Poly(dc, other.Outline(), SelPen);

        if (scene.ItemFor(Selected) is { } sel)
        {
            Poly(dc, sel.Outline(), SelPen);
            if (SelectedGroup == null) TitleLabel(dc, sel, SelPen.Brush);
            if (RadiusGroup().Count > 0)
            {
                var c = ToScreen(sel.Location);
                Cross(dc, c, SelPen, 8);
                double rs = sel.Radius * _zoom;
                if (rs > 0 && sel.Tilt != null) TiltedCircle(dc, sel, sel.Radius, HoverPen);
                else if (rs > 0) dc.DrawEllipse(null, HoverPen, c, rs, rs);
                if (!EditLocked)
                {
                    var h = RadiusHandle(sel);
                    dc.DrawRectangle(HandleBrush, null, new Rect(h.X - 5, h.Y - 5, 10, 10));
                    int n = RadiusGroup().Count;
                    Label(dc, $"半径 {PanelValues.Num(Math.Round(sel.Radius, 1))}{(n > 1 ? $"（{n} 本まとめて）" : "")}　黄色い四角をドラッグ・＋／－ キー", h + new Vector(10, -9), HandleBrush);
                }
                // 最小・最大の角度の線
                dc.DrawLine(LimitPen, c, ToScreen(sel.Project(OnCircle(sel.Location, sel.Radius, sel.InitialAngle))));
                dc.DrawLine(LimitPen, c, ToScreen(sel.Project(OnCircle(sel.Location, sel.Radius, sel.LastAngle))));
            }
        }
    }

    /// <summary>計器の名前を枠の左上に出す。</summary>
    private void TitleLabel(DrawingContext dc, RenderItem item, Brush brush)
    {
        var text = item.Title.Length > 0 ? item.Title : $"{PanelSpec.KindLabel(item.Kind)} {item.Subject}";
        var pts = item.Outline().Select(ToScreen).ToArray();
        var at = new Point(pts.Min(p => p.X), pts.Min(p => p.Y) - 18);
        Label(dc, text, at, brush);
    }

    /// <summary>グループの計器全部を囲む枠と名前。</summary>
    private void GroupBox(DrawingContext dc, string group, Pen pen, Brush labelBrush, string label)
    {
        var pts = MembersOf(group).Select(i => Scene!.ItemFor(i)).Where(it => it != null).SelectMany(it => it!.Outline()).Select(ToScreen).ToList();
        if (pts.Count == 0) return;
        var r = new Rect(new Point(pts.Min(p => p.X) - 4, pts.Min(p => p.Y) - 4), new Point(pts.Max(p => p.X) + 4, pts.Max(p => p.Y) + 4));
        dc.DrawRectangle(null, pen, r);
        Label(dc, label, r.TopLeft + new Vector(0, -18), labelBrush);
    }

    private readonly Dictionary<int, double> _radiusStarts = [];

    /// <summary>確認用: ＋／－ キーと同じく半径を変える（step px）。変えたら true。</summary>
    public bool StepRadiusForTest(int step)
    {
        if (RadiusGroup().Count == 0 || Scene?.ItemFor(Selected) is not { } sel || EditLocked) return false;
        SetRadii(Math.Max(1, sel.Radius + step) / Math.Max(1e-6, sel.Radius), fromStart: false);
        EditCommitted?.Invoke(Selected);
        return true;
    }

    /// <summary>
    /// 半径を変えられる計器: 選んでいる指針・円形ゲージ 1 つか、同じ回転の中心（Location）にある指針・円形ゲージ全部（時計の 3 本、圧力計の 2 本など）。
    /// それ以外の選び方なら空。
    /// </summary>
    private List<RenderItem> RadiusGroup()
    {
        if (Scene == null || Scene.ItemFor(Selected) is not { } sel || sel.Kind is not (SectionKind.Needle or SectionKind.DigitalGauge)) return [];
        var items = _set.Select(i => Scene.ItemFor(i)).OfType<RenderItem>().ToList();
        if (items.Count <= 1) return [sel];
        return items.All(it => it.Kind is SectionKind.Needle or SectionKind.DigitalGauge && (it.Location - sel.Location).Length < 0.5) ? items : [];
    }

    /// <summary>半径を ratio 倍にする（まとめて選んだ針は大きさの比をそのままに）。fromStart ならドラッグを始めたときの半径から。</summary>
    private void SetRadii(double ratio, bool fromStart)
    {
        if (Scene == null || Document == null) return;
        foreach (var it in RadiusGroup())
        {
            double baseR = fromStart && _radiusStarts.TryGetValue(it.Section, out var st) ? st : it.Radius;
            double r = Math.Max(1, Math.Round(baseR * ratio, 1));
            it.Radius = r;
            if (it.Kind == SectionKind.Needle && it.ImageOrigin.Y > 0) it.Scale = r / it.ImageOrigin.Y;
            Document.Sections[it.Section].Set("Radius", PanelValues.Num(r));
        }
        InvalidateVisual();
    }

    private Point RadiusHandle(RenderItem sel) => ToScreen(new Point(sel.Location.X, sel.Location.Y - Math.Max(sel.Radius, 4)));

    private void Poly(DrawingContext dc, Point[] pts, Pen pen)
    {
        for (int i = 0; i < pts.Length; i++)
            dc.DrawLine(pen, ToScreen(pts[i]), ToScreen(pts[(i + 1) % pts.Length]));
    }

    /// <summary>中心・消失点の印。選んでいるものは大きくし、まわりに輪と薄い塗りを付ける。</summary>
    private static void DrawPoint(DrawingContext dc, Point c, Pen pen, bool active, bool diagonal)
    {
        if (active)
        {
            var fill = pen.Brush.Clone();
            fill.Opacity = 0.18;
            fill.Freeze();
            dc.DrawEllipse(fill, pen, c, 16, 16);
            Cross(dc, c, pen, 14, diagonal);
        }
        else Cross(dc, c, pen, 9, diagonal);
    }

    private static void Cross(DrawingContext dc, Point c, Pen pen, double s, bool diagonal = false)
    {
        if (diagonal)
        {
            dc.DrawLine(pen, c + new Vector(-s, -s), c + new Vector(s, s));
            dc.DrawLine(pen, c + new Vector(-s, s), c + new Vector(s, -s));
        }
        else
        {
            dc.DrawLine(pen, c + new Vector(-s, 0), c + new Vector(s, 0));
            dc.DrawLine(pen, c + new Vector(0, -s), c + new Vector(0, s));
        }
        dc.DrawEllipse(null, pen, c, s / 2.5, s / 2.5);
    }

    private void Label(DrawingContext dc, string text, Point at, Brush brush)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Yu Gothic UI"), 11,
            brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0)), null, new Rect(at, new Size(ft.Width + 4, ft.Height)));
        dc.DrawText(ft, at + new Vector(2, 0));
    }

    // ---- アニメーション ----

    private void OnFrame(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs re || re.RenderingTime == _lastFrame) return;
        double dt = _lastFrame == TimeSpan.Zero ? 0 : (re.RenderingTime - _lastFrame).TotalSeconds;
        _lastFrame = re.RenderingTime;
        dt = Math.Min(dt, 0.1);
        if (dt > 0) Ticked?.Invoke(dt);
        if (Scene == null) return;

        bool changed = false;
        foreach (var it in Scene.Items)
        {
            if (it.Kind != SectionKind.Needle) continue;
            double target = it.TargetAngle(ValueOf(it));
            double before = it.Angle;
            if (!it.AngleReady || it.NaturalFreq <= 0 || dt <= 0)
            {
                it.Angle = target;
                it.AngularVelocity = 0;
                it.AngleReady = true;
            }
            else
            {
                // 2 次遅れ: θ'' = ω²(目標 − θ) − 2ζωθ'
                double w = it.NaturalFreq, z = Math.Max(it.DampingRatio, 0);
                int n = Math.Max(1, (int)Math.Ceiling(dt * w * 4));
                double h = dt / n;
                for (int k = 0; k < n; k++)
                {
                    double acc = w * w * (target - it.Angle) - 2 * z * w * it.AngularVelocity;
                    it.AngularVelocity += acc * h;
                    it.Angle += it.AngularVelocity * h;
                }
                if (Math.Abs(target - it.Angle) < 1e-3 && Math.Abs(it.AngularVelocity) < 1e-2)
                {
                    it.Angle = target;
                    it.AngularVelocity = 0;
                }
            }
            if (Math.Abs(it.Angle - before) > 1e-4) changed = true;
        }
        if (changed) InvalidateVisual();
    }

    // ---- マウス・キー ----

    private int HitItem(Point panel)
    {
        if (Scene == null) return -1;
        for (int i = Scene.Items.Count - 1; i >= 0; i--)
        {
            var it = Scene.Items[i];
            if (it.Hidden || !it.HitTest(panel)) continue;
            // メインパネルは選ばない（全体を覆うので、何もない所と同じく [This] にする）。
            // 絵のある所ならそこで止め、透明な所（窓の穴など）なら下の計器を選べるようにする
            if (it.IsMainPanel)
            {
                if (OpaqueAt(it, panel)) return -1;
                continue;
            }
            return it.Section;
        }
        return -1;
    }

    /// <summary>確認用: パネルの点 p をクリックしたときに選ぶ計器（-1 = [This]）。</summary>
    public int HitForTest(Point p) => HitItem(p);

    /// <summary>表示灯の画像の、パネルの点 p の所が透明でないか（画像がなければ false）。</summary>
    private bool OpaqueAt(RenderItem it, Point p)
    {
        var bmp = NightAmount >= 0.5 && it.Night != null ? it.Night : it.Day;
        if (bmp == null) return false;
        int x = (int)Math.Floor(p.X - it.Location.X), y = (int)Math.Floor(p.Y - it.Location.Y);
        if (x < 0 || y < 0 || x >= bmp.PixelWidth || y >= bmp.PixelHeight) return false;
        var px = new byte[4];
        try { new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0); }
        catch (ArgumentException) { return true; }
        return px[3] >= 32;
    }

    private static bool Near(Point a, Point b) => (a - b).Length <= 7;

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (Scene == null || Document == null) return;
        var sp = e.GetPosition(this);
        var pp = ToPanel(sp);
        _dragStartScreen = sp;
        _dragStartPanel = pp;
        _dragStartOffset = _offset;
        _dragMoved = false;

        if (e.ChangedButton is MouseButton.Middle or MouseButton.Right)
        {
            _drag = DragMode.Pan;
            CaptureMouse();
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;

        // Ctrl＋ドラッグ: 位置ではなく、その計器の値（状態量）を動かしてプレビューする。ロック中でも使える
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            int vh = HitItem(pp);
            if (vh >= 0)
            {
                Select(vh, raise: true);
                _drag = DragMode.Value;
                _valueSection = vh;
                CaptureMouse();
                ValueDragStarted?.Invoke(vh);
                return;
            }
        }

        // 選択中の計器・パネル本体のハンドル（ロック中・複数選択中は使わない）
        if (!EditLocked && RadiusGroup() is { Count: > 0 } rg && Scene.ItemFor(Selected) is { } sel && Near(sp, RadiusHandle(sel)))
        {
            _radiusStarts.Clear();
            foreach (var it in rg) _radiusStarts[it.Section] = it.Radius;
            _drag = DragMode.Radius;
            CaptureMouse();
            return;
        }
        if (Selected >= 0 && Selected == Scene.ThisSection)
        {
            bool nearC = Scene.HasCenter && Near(sp, ToScreen(Scene.Center)), nearO = Scene.HasOrigin && Near(sp, ToScreen(Scene.Origin));
            // 中心と消失点が重なっているときは、押すたびに入れ替える
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            var point = nearC && nearO
                ? (shift ? PanelPoint.Both : ActivePoint == PanelPoint.Center ? PanelPoint.Origin : PanelPoint.Center)
                : nearC ? PanelPoint.Center : nearO ? PanelPoint.Origin : PanelPoint.None;
            if (point != PanelPoint.None)
            {
                if (shift)
                {
                    // Shift＋クリック: 選択に足す・外す（計器と同じ。ドラッグしても動かさない）
                    ActivePoint = point == PanelPoint.Both ? PanelPoint.Both : ActivePoint ^ point;
                    _drag = DragMode.Blocked;
                    _blockedSilently = true;
                    CaptureMouse();
                    return;
                }
                // 押した点を選ぶ。両方選んでいて、そのうちの 1 つを押したなら両方のまま（ドラッグで一緒に動く）
                if (!ActivePoint.HasFlag(point) || ActivePoint != PanelPoint.Both) ActivePoint = point;
                if (PositionLocked) { _drag = DragMode.Blocked; _blockedSilently = true; CaptureMouse(); return; }
                if (point == PanelPoint.Center) StartPointDrag(DragMode.Center, Scene.Center);
                else StartPointDrag(DragMode.Origin, Scene.Origin);
                return;
            }
            ActivePoint = PanelPoint.None;
        }

        int hit = SelectAt(pp, e.ClickCount >= 2, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        if (hit >= 0)
        {
            if (EditLocked || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                // 選ぶだけ。ドラッグしても動かさない
                _drag = DragMode.Blocked;
                _blockedSilently = !EditLocked;
                CaptureMouse();
                return;
            }
            StartMoveDrag();
            return;
        }
        _drag = DragMode.Marquee;
        _marqueeEnd = sp;
        _marqueeBase = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? _set.ToList() : [];
        CaptureMouse();
    }

    /// <summary>
    /// 囲み選択: 四角に丸ごと入っている計器を選ぶ。グループは中身が全部入ったときだけ（中に入ったグループは 1 つずつ）。
    /// </summary>
    public void SelectInRect(Rect panelRect, bool add)
    {
        if (Scene == null || Document == null) return;
        var inside = new HashSet<int>();
        foreach (var it in Scene.Items)
        {
            if (it.IsMainPanel || it.Hidden) continue; // メインパネル・非表示の計器は囲んでも選ばない
            var o = it.Outline();
            var b = new Rect(new Point(o.Min(q => q.X), o.Min(q => q.Y)), new Point(o.Max(q => q.X), o.Max(q => q.Y)));
            if (panelRect.Contains(b)) inside.Add(it.Section);
        }
        var result = new List<int>();
        foreach (var i in inside.Order())
        {
            var g = GroupOf(i);
            if (g == null || g == EnteredGroup) { result.Add(i); continue; }
            var m = MembersOf(g);
            if (m.All(inside.Contains) && !result.Contains(i)) result.AddRange(m.Where(x => !result.Contains(x)));
        }
        if (add) result = _marqueeBase.Concat(result).Distinct().ToList();
        result.Sort();
        if (result.Count == 0)
        {
            if (!add) SetSelection(Scene.ThisSection, Scene.ThisSection >= 0 ? [Scene.ThisSection] : [], null);
        }
        else
        {
            var groups = result.Select(GroupOf).Distinct().ToList();
            string? grp = groups.Count == 1 && groups[0] is { } only && MembersOf(only).Count == result.Count ? only : null;
            SetSelection(result[0], result, grp, grp == null && EnteredGroup != null && result.All(i => GroupOf(i) == EnteredGroup) ? EnteredGroup : null);
        }
        SelectionChanged?.Invoke(Selected);
    }

    /// <summary>選んでいる計器すべての元の位置を覚えてから動かし始める。</summary>
    private void StartMoveDrag()
    {
        _moveStarts.Clear();
        foreach (var i in _set)
            if (Scene!.ItemFor(i) is { } it) _moveStarts[i] = it.Location;
        _drag = DragMode.Move;
        CaptureMouse();
    }

    private readonly Dictionary<int, Point> _moveStarts = [];
    private bool _blockedSilently;

    private void StartPointDrag(DragMode mode, Point start)
    {
        _drag = mode;
        _dragStartValue = start;
        // 両方選んでいるときは、もう一方も一緒に動かす
        _dragStartOther = mode == DragMode.Center ? Scene!.Origin : Scene!.Center;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Scene == null || Document == null) return;
        var sp = e.GetPosition(this);
        var pp = ToPanel(sp);
        PanelMouseMove?.Invoke(pp);

        if (_drag == DragMode.None)
        {
            int h = HitItem(pp);
            if (h != _hover) { _hover = h; InvalidateVisual(); }
            Cursor = h >= 0 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? Cursors.SizeNS
                : EditLocked && !(h >= 0 && _fresh.Contains(h) && _set.Contains(h)) ? (h >= 0 ? Cursors.Hand : Cursors.Arrow)
                : RadiusGroup().Count > 0 && Scene.ItemFor(Selected) is { } s && Near(sp, RadiusHandle(s))
                ? Cursors.SizeNS : h >= 0 ? Cursors.SizeAll : Cursors.Arrow;
            return;
        }

        if ((sp - _dragStartScreen).Length > 2)
        {
            if (!_dragMoved && _drag == DragMode.Blocked && !_blockedSilently) LockedEditAttempt?.Invoke();
            _dragMoved = true;
        }
        if (!_dragMoved || _drag == DragMode.Blocked) return;
        if (_drag == DragMode.Value)
        {
            ValueDragged?.Invoke(_valueSection, _dragStartScreen.Y - sp.Y, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            return;
        }

        switch (_drag)
        {
            case DragMode.Marquee:
                _marqueeEnd = sp;
                InvalidateVisual();
                break;

            case DragMode.Pan:
                _offset = _dragStartOffset + (sp - _dragStartScreen);
                InvalidateVisual();
                break;

            case DragMode.Move:
                {
                    var d = pp - _dragStartPanel;
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    {
                        if (Math.Abs(d.X) > Math.Abs(d.Y)) d.Y = 0; else d.X = 0;
                    }
                    MoveSelected(new Vector(Math.Round(d.X), Math.Round(d.Y)), fromStart: true);
                    break;
                }

            case DragMode.Center:
            case DragMode.Origin:
                {
                    var d = pp - _dragStartPanel;
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    {
                        if (Math.Abs(d.X) > Math.Abs(d.Y)) d.Y = 0; else d.X = 0;
                    }
                    var np = new Point(Math.Round(_dragStartValue.X + d.X), Math.Round(_dragStartValue.Y + d.Y));
                    ApplyPoint(_drag, np);
                    if (ActivePoint == PanelPoint.Both && Scene.HasCenter && Scene.HasOrigin)
                        ApplyPoint(_drag == DragMode.Center ? DragMode.Origin : DragMode.Center,
                            new Point(Math.Round(_dragStartOther.X + d.X), Math.Round(_dragStartOther.Y + d.Y)));
                    break;
                }

            case DragMode.Radius:
                if (Scene.ItemFor(Selected) is { } it && _radiusStarts.TryGetValue(Selected, out var r0) && r0 > 0)
                {
                    double r = Math.Max(1, Math.Round((pp - it.Location).Length));
                    SetRadii(r / r0, fromStart: true);
                }
                break;
        }
    }

    /// <summary>
    /// 選んでいる計器をまとめて動かす。fromStart なら押した時の位置から d だけ、そうでなければ今の位置から d だけ。
    /// </summary>
    private void MoveSelected(Vector d, bool fromStart)
    {
        if (Scene == null || Document == null) return;
        foreach (var i in _set)
        {
            if (Scene.ItemFor(i) is not { } it) continue;
            var start = fromStart && _moveStarts.TryGetValue(i, out var st) ? st : it.Location;
            var np = new Point(start.X + d.X, start.Y + d.Y);
            it.Location = np;
            Document.Sections[i].Set("Location", PanelValues.Point(np.X, np.Y));
        }
        InvalidateVisual();
    }

    private void ApplyPoint(DragMode mode, Point np)
    {
        if (Scene == null || Document == null || Selected < 0) return;
        var sec = Document.Sections[Selected];
        switch (mode)
        {
            case DragMode.Move:
                if (Scene.ItemFor(Selected) is { } it)
                {
                    it.Location = np;
                    sec.Set("Location", PanelValues.Point(np.X, np.Y));
                }
                break;
            case DragMode.Center:
                Scene.Center = np;
                sec.Set("Center", PanelValues.Point(np.X, np.Y));
                break;
            case DragMode.Origin:
                Scene.Origin = np;
                sec.Set("Origin", PanelValues.Point(np.X, np.Y));
                break;
        }
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag == DragMode.None) return;
        FinishDrag(release: true);
    }

    /// <summary>つかみが外れた（Alt+Tab・ほかの窓が出たなど）ときも、離したのと同じに確定する（動かした位置がテキストに入らないのを防ぐ）。</summary>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag != DragMode.None) FinishDrag(release: false);
    }

    private void FinishDrag(bool release)
    {
        var mode = _drag;
        _drag = DragMode.None;
        if (release) ReleaseMouseCapture();
        if (mode == DragMode.Value)
        {
            if (!_dragMoved) ValueClicked?.Invoke(_valueSection);
            return;
        }
        _blockedSilently = false;
        if (mode == DragMode.Marquee)
        {
            if (_dragMoved) SelectInRect(new Rect(ToPanel(_dragStartScreen), ToPanel(_marqueeEnd)), add: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || _marqueeBase.Count > 0);
            InvalidateVisual();
            return;
        }
        if (_dragMoved && mode is not (DragMode.Pan or DragMode.Blocked) && Selected >= 0) EditCommitted?.Invoke(Selected);
        if (mode == DragMode.Pan) ViewChanged?.Invoke();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover >= 0) { _hover = -1; InvalidateVisual(); }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Scene == null) return;
        ZoomAt(_zoom * (e.Delta > 0 ? 1.2 : 1 / 1.2), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Scene == null || Document == null || Selected < 0) return;
        var d = e.Key switch
        {
            Key.Left => new Vector(-1, 0),
            Key.Right => new Vector(1, 0),
            Key.Up => new Vector(0, -1),
            Key.Down => new Vector(0, 1),
            _ => default,
        };
        if (d == default)
        {
            // ＋／－: 指針・円形ゲージの半径（大きさ）を 1px（Shift で 5px）ずつ
            int step = e.Key is Key.OemPlus or Key.Add ? 1 : e.Key is Key.OemMinus or Key.Subtract ? -1 : 0;
            if (step == 0 || RadiusGroup().Count == 0 || Scene.ItemFor(Selected) is not { } rsel) return;
            if (EditLocked) { LockedEditAttempt?.Invoke(); e.Handled = true; return; }
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) step *= 5;
            double nr = Math.Max(1, rsel.Radius + step);
            SetRadii(nr / Math.Max(1e-6, rsel.Radius), fromStart: false);
            EditCommitted?.Invoke(Selected);
            e.Handled = true;
            return;
        }
        // 中心・消失点はいつもの位置ロックのまま。計器は追加したばかりのものだけ動かせる
        if (Selected == Scene.ThisSection && ActivePoint != PanelPoint.None ? PositionLocked : EditLocked)
        {
            LockedEditAttempt?.Invoke();
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) d *= 10;
        // 選んでいる中心・消失点を動かす
        if (MoveActivePoint(d)) { e.Handled = true; return; }
        if (_set.Any(i => Scene.ItemFor(i) != null))
        {
            MoveSelected(d, fromStart: false);
            EditCommitted?.Invoke(Selected);
            e.Handled = true;
        }
    }
}
