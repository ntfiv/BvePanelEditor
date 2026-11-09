using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BvePanelEditor.Core;
using BvePanelEditor.Core.Psd;

namespace BvePanelEditor.Rendering;

/// <summary>描画する計器 1 つ（パネルファイルの 1 セクション）。</summary>
public sealed class RenderItem
{
    public int Section;
    public SectionKind Kind;
    public string Subject = "";
    /// <summary>名前（直前のコメント）。</summary>
    public string Title = "";
    public int Layer;
    /// <summary>メインパネル（パネルの土台の表示灯）。パネル画面のクリックでは選ばない。</summary>
    public bool IsMainPanel;
    /// <summary>一覧で非表示にした（描かない・クリックで選ばない）。</summary>
    public bool Hidden;
    public BitmapSource? Day;
    public BitmapSource? Night;
    public Point Location;

    // Needle
    public Point ImageOrigin;
    public double Scale = 1;
    public double InitialAngle, LastAngle, Minimum, Maximum = 1;
    public bool StopPin;
    public double NaturalFreq, DampingRatio;
    public double Angle, AngularVelocity;
    public bool AngleReady;

    // Needle / DigitalGauge
    public double Radius;
    /// <summary>Tilt（書式 2.01）の angleX, angleY。0, 0 なら平ら。</summary>
    public double TiltX, TiltY;
    /// <summary>[This] の Resolution・Perspective（Tilt の遠近に使う）。</summary>
    public double TiltResolution, TiltPerspective = 1;
    /// <summary>遠近の中心（[This] の Origin。なければ Center。どちらもなければ計器の位置）。</summary>
    public Point? TiltCamera;
    private TiltProjection? _tilt;
    private Point _tiltAt;

    /// <summary>Tilt の近似の写し方（平らなら null）。Location が変わったら作り直す。</summary>
    public TiltProjection? Tilt
    {
        get
        {
            if (Kind is not (SectionKind.Needle or SectionKind.DigitalGauge) || (Math.Abs(TiltX) < 1e-9 && Math.Abs(TiltY) < 1e-9)) return null;
            if (_tilt == null || _tiltAt != Location)
            {
                var cam = TiltCamera ?? Location;
                _tilt = TiltProjection.Create(Location.X, Location.Y, TiltX, TiltY, TiltResolution, TiltPerspective, cam.X, cam.Y);
                _tiltAt = Location;
            }
            return _tilt;
        }
    }

    /// <summary>平らなときのパネル座標の点を、Tilt で傾けた点へ（平らならそのまま）。</summary>
    public Point Project(Point p)
    {
        if (Tilt is not { } t) return p;
        var (x, y) = t.Project(p.X, p.Y);
        return new Point(x, y);
    }

    /// <summary>画面（パネル座標）の点を、平らなときの点へ戻す。</summary>
    public Point Unproject(Point p)
    {
        if (Tilt is not { } t) return p;
        var (x, y) = t.Unproject(p.X, p.Y);
        return new Point(x, y);
    }

    // DigitalGauge
    public Color GaugeColor = Colors.White;
    public double Step;

    // DigitalNumber
    public int Interval;
    public int Frames = 1;

    public double ImageWidth => Day?.PixelWidth ?? Night?.PixelWidth ?? 32;
    public double ImageHeight => Day?.PixelHeight ?? Night?.PixelHeight ?? 32;

    public double TargetAngle(double value)
    {
        double span = Maximum - Minimum;
        double t = Math.Abs(span) < 1e-12 ? 0 : (value - Minimum) / span;
        if (StopPin || Kind == SectionKind.DigitalGauge) t = Math.Clamp(t, 0, 1);
        return InitialAngle + t * (LastAngle - InitialAngle);
    }

    /// <summary>針画像を置くための変換（画像座標 → パネル座標）。</summary>
    public Matrix NeedleMatrix(double angle)
    {
        var m = Matrix.Identity;
        m.Translate(-ImageOrigin.X, -ImageOrigin.Y);
        m.Scale(Scale, Scale);
        m.Rotate(angle);
        m.Translate(Location.X, Location.Y);
        return m;
    }

    /// <summary>画面に描かれる範囲（パネル座標の多角形）。選択枠と当たり判定に使う。</summary>
    public Point[] Outline()
    {
        switch (Kind)
        {
            case SectionKind.Needle:
                {
                    var m = NeedleMatrix(AngleReady ? Angle : InitialAngle);
                    double w = ImageWidth, h = ImageHeight;
                    return [Project(m.Transform(new Point(0, 0))), Project(m.Transform(new Point(w, 0))), Project(m.Transform(new Point(w, h))), Project(m.Transform(new Point(0, h)))];
                }
            case SectionKind.DigitalGauge:
                {
                    double r = Math.Max(Radius, 4);
                    return [Project(new(Location.X - r, Location.Y - r)), Project(new(Location.X + r, Location.Y - r)), Project(new(Location.X + r, Location.Y + r)), Project(new(Location.X - r, Location.Y + r))];
                }
            default:
                {
                    double w = ImageWidth, h = Kind == SectionKind.DigitalNumber && Interval > 0 ? Interval : ImageHeight;
                    var p = Location;
                    return [p, new(p.X + w, p.Y), new(p.X + w, p.Y + h), new(p.X, p.Y + h)];
                }
        }
    }

    public bool HitTest(Point p)
    {
        p = Unproject(p);
        if (double.IsNaN(p.X)) return false;
        if (Kind == SectionKind.DigitalGauge)
            return (p - Location).Length <= Math.Max(Radius, 6);
        if (Kind == SectionKind.Needle)
        {
            var m = NeedleMatrix(AngleReady ? Angle : InitialAngle);
            if (!m.HasInverse) return false;
            m.Invert();
            var q = m.Transform(p);
            return q.X >= 0 && q.Y >= 0 && q.X <= ImageWidth && q.Y <= ImageHeight;
        }
        var o = Outline();
        return p.X >= o[0].X && p.Y >= o[0].Y && p.X <= o[2].X && p.Y <= o[2].Y;
    }
}

/// <param name="BveEx">BveEX 対応車両で使える書き方（ats256 以上など）。警告ではなく、別の印で知らせる。</param>
public sealed record Problem(int Section, string Message, bool BveEx = false);

/// <summary>パネルファイルから作った描画用データ一式。</summary>
public sealed class PanelScene
{
    public BitmapSource? BaseDay;
    public BitmapSource? BaseNight;
    public int ThisSection = -1;
    /// <summary>[This] を一覧で非表示にした（パネル本体の画像を描かない）。</summary>
    public bool ThisHidden;
    public Point Center, Origin;
    public bool HasCenter, HasOrigin;
    public Rect ViewLimit = Rect.Empty;
    public double Resolution;
    /// <summary>[This] の Perspective（書式 2.01。Tilt の遠近の強さ、既定 1）。</summary>
    public double Perspective = 1;
    public List<RenderItem> Items = [];
    public List<Problem> Problems = [];

    /// <summary>パネル本体の画像の中身の大きさ（2 の累乗に広げて書き出した PNG なら広げる前の大きさ）。</summary>
    public Size? BaseContent;

    public Size ContentSize
    {
        get
        {
            if (BaseContent is { } c) return c;
            if (BaseDay != null) return new Size(BaseDay.PixelWidth, BaseDay.PixelHeight);
            if (BaseNight != null) return new Size(BaseNight.PixelWidth, BaseNight.PixelHeight);
            double w = 1024, h = 768;
            foreach (var it in Items)
                foreach (var p in it.Outline()) { w = Math.Max(w, p.X); h = Math.Max(h, p.Y); }
            return new Size(w, h);
        }
    }

    public RenderItem? ItemFor(int section) => Items.FirstOrDefault(i => i.Section == section);

    /// <summary>
    /// パネルの見本を作る。variation を指定すると、そのバリエーションに入らない計器は描かない（[This] もそのバリエーションのもの）。
    /// </summary>
    public static PanelScene Build(PanelDocument doc, string? baseDir, ImageCache cache, PanelScene? previous, string? variation = null)
    {
        var scene = new PanelScene();
        // 書式 2.01 で足されたキー（Tilt・StopPin・Perspective）は、ヘッダが 2.00 でも BVE（5.8 以降・6）は読む
        const bool v201 = true;
        if (!doc.HasPanelHeader && !doc.IsLegacyHeader)
            scene.Problems.Add(new(-1, $"1 行目が「{PanelDocument.HeaderPrefix} 2.00」の形になっていません"));

        int thisCount = 0;
        var thisSections = new List<PanelSection>();
        for (int i = 0; i < doc.Sections.Count; i++)
        {
            var s = doc.Sections[i];
            var kind = s.Kind;
            void P(string msg) => scene.Problems.Add(new(i, msg));
            // コメントアウトした計器は BVE でも表示されないので、描かず、問題も調べない
            if (s.Disabled) continue;
            // 表示中のバリエーションに入らない計器（[This] は使わない、ほかは描かない）
            bool outOfVariation = variation != null && !s.InVariation(variation);
            if (outOfVariation && kind == SectionKind.This) continue;

            if (kind == SectionKind.Unknown)
            {
                P($"[{s.Name}] は BVE5/6 のパネルにないセクションです");
                continue;
            }
            var specs = PanelSpec.For(kind);
            foreach (var kv in s.KeyValues)
            {
                var spec = specs.FirstOrDefault(k => k.Name.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
                if (spec == null) P($"{kv.Key} は [{s.Name}] で使えないキーです");
            }
            foreach (var l in s.Lines.Where(l => l.Kind == PanelLineKind.Unknown))
                P($"読めない行: {l.Raw.Trim()}");

            (byte, byte, byte)? transparent = PanelValues.TryColor(s.Get("TransparentColor"), out var tr, out var tg, out var tb) ? (tr, tg, tb) : null;
            BitmapSource? LoadImg(string key, (byte, byte, byte)? tint, bool required)
            {
                var rel = s.Get(key);
                if (string.IsNullOrWhiteSpace(rel))
                {
                    if (required) P($"{key} がありません");
                    return null;
                }
                if (baseDir == null) return null;
                var full = Path.GetFullPath(Path.Combine(baseDir, rel.Trim().Trim('"')));
                var img = cache.Get(full, transparent, tint);
                if (img == null) P(File.Exists(full) ? $"画像を読めません: {rel}" : $"画像がありません: {rel}");
                return img;
            }

            if (kind == SectionKind.This)
            {
                thisCount++;
                if (thisCount > 1)
                {
                    // バリエーションごとに違う [This]（同じバリエーションに 2 つ入らない）なら問題にしない
                    bool clash = doc.Variations.Count == 0 || doc.VariationNames.Any(v => s.InVariation(v) && thisSections.Any(t => t.InVariation(v)));
                    thisSections.Add(s);
                    if (clash) P("[This] が 2 つ以上あります（1 つだけにしてください）");
                    continue;
                }
                thisSections.Add(s);
                scene.ThisSection = i;
                scene.ThisHidden = s.ViewHidden;
                // [This] の画像はなくてもよい（BVE も読める。パネル本体はメインパネルの表示灯にする書き方）
                scene.BaseDay = LoadImg("DaytimeImage", null, false);
                scene.BaseNight = LoadImg("NighttimeImage", null, false);
                foreach (var key in new[] { "DaytimeImage", "NighttimeImage" })
                {
                    if (baseDir == null || s.Get(key) is not { Length: > 0 } rel) continue;
                    var full = Path.GetFullPath(Path.Combine(baseDir, rel.Trim().Trim('"')));
                    if (PngWriter.TryReadContentSize(full, out var cw, out var ch)) { scene.BaseContent = new Size(cw, ch); break; }
                }
                scene.HasCenter = PanelValues.TryPoint(s.Get("Center"), out var cx, out var cy);
                scene.Center = new Point(cx, cy);
                scene.HasOrigin = PanelValues.TryPoint(s.Get("Origin"), out var ox, out var oy);
                scene.Origin = new Point(ox, oy);
                scene.Resolution = PanelValues.Double(s.Get("Resolution"), 0);
                if (v201 && PanelValues.TryDouble(s.Get("Perspective"), out var per) && per > 0) scene.Perspective = per;
                if (PanelValues.TryDouble(s.Get("Left"), out var l) && PanelValues.TryDouble(s.Get("Top"), out var t)
                    && PanelValues.TryDouble(s.Get("Right"), out var r) && PanelValues.TryDouble(s.Get("Bottom"), out var b) && r > l && b > t)
                    scene.ViewLimit = new Rect(l, t, r - l, b - t);
                continue;
            }

            var item = new RenderItem { Section = i, Kind = kind, IsMainPanel = s.IsMainPanel && kind == SectionKind.PilotLamp, Hidden = s.ViewHidden || outOfVariation };
            item.Subject = s.Get("Subject") ?? "";
            item.Title = s.Title;
            if (item.Subject.Length == 0) P("Subject がありません");
            else if (Subjects.IsBveExAts(item.Subject, out var exAts))
                scene.Problems.Add(new(i, $"Subject「{item.Subject}」は BveEX の状態量です（ats{exAts}。BVE 本体の ats は 0〜255）", BveEx: true));
            else if (!Subjects.IsKnown(item.Subject)) P($"Subject「{item.Subject}」は BVE の状態量にありません");
            item.Layer = (int)PanelValues.Double(s.Get("Layer"), 0);
            if (!PanelValues.TryPoint(s.Get("Location"), out var lx, out var ly)) P("Location がありません");
            item.Location = new Point(lx, ly);

            switch (kind)
            {
                case SectionKind.PilotLamp:
                    item.Day = LoadImg("DaytimeImage", null, true);
                    item.Night = LoadImg("NighttimeImage", null, false);
                    break;

                case SectionKind.DigitalNumber:
                    item.Day = LoadImg("DaytimeImage", null, true);
                    item.Night = LoadImg("NighttimeImage", null, false);
                    item.Interval = (int)PanelValues.Double(s.Get("Interval"), 0);
                    if (item.Interval <= 0) P("Interval（1 コマの高さ）がありません");
                    else item.Frames = Math.Max(1, (int)(item.ImageHeight / item.Interval));
                    break;

                case SectionKind.Needle:
                    {
                        (byte, byte, byte)? tint = PanelValues.TryColor(s.Get("Color"), out var cr, out var cg, out var cb) && (cr, cg, cb) != (255, 255, 255) ? (cr, cg, cb) : null;
                        item.Day = LoadImg("DaytimeImage", tint, true);
                        item.Night = LoadImg("NighttimeImage", tint, false);
                        item.ImageOrigin = PanelValues.TryPoint(s.Get("Origin"), out var iox, out var ioy)
                            ? new Point(iox, ioy) : new Point(item.ImageWidth / 2, item.ImageHeight / 2);
                        double natural = item.ImageOrigin.Y;
                        item.Radius = PanelValues.Double(s.Get("Radius"), natural);
                        item.Scale = natural > 0 && item.Radius > 0 ? item.Radius / natural : 1;
                        ReadRange(s, item, P);
                        item.StopPin = PanelValues.TryBool(s.Get("StopPin"), out var sp) && sp && v201;
                        item.NaturalFreq = PanelValues.Double(s.Get("NaturalFreq"), 0);
                        item.DampingRatio = PanelValues.Double(s.Get("DampingRatio"), 0);
                        break;
                    }

                case SectionKind.DigitalGauge:
                    item.Radius = PanelValues.Double(s.Get("Radius"), 0);
                    if (item.Radius <= 0) P("Radius がありません");
                    item.GaugeColor = PanelValues.TryColor(s.Get("Color"), out var gr, out var gg, out var gb) ? Color.FromRgb(gr, gg, gb) : Colors.White;
                    ReadRange(s, item, P);
                    item.Step = PanelValues.Double(s.Get("Step"), 0);
                    break;
            }

            // Tilt（書式 2.01）は針・デジタルゲージだけ
            if (v201 && kind is SectionKind.Needle or SectionKind.DigitalGauge && PanelValues.TryPoint(s.Get("Tilt"), out var tiltX, out var tiltY))
            {
                item.TiltX = tiltX;
                item.TiltY = tiltY;
            }

            var old = previous?.ItemFor(i);
            if (old != null && old.Kind == item.Kind && old.AngleReady)
            {
                item.Angle = old.Angle;
                item.AngularVelocity = old.AngularVelocity;
                item.AngleReady = true;
            }
            scene.Items.Add(item);
        }

        if (thisCount == 0) scene.Problems.Add(new(-1, "[This] がありません"));
        Point? camera = scene.HasOrigin ? scene.Origin : scene.HasCenter ? scene.Center : null;
        foreach (var it in scene.Items) { it.TiltResolution = scene.Resolution; it.TiltPerspective = scene.Perspective; it.TiltCamera = camera; }
        foreach (var w in doc.GroupWarnings) scene.Problems.Add(new(w.Section, w.Message));
        // バリエーション
        foreach (var n in doc.UnknownVariationNames())
            scene.Problems.Add(new(doc.Sections.FindIndex(s => VariationCondition.Names(s.VariationCond).Contains(n)), $"バリエーション「{n}」は一覧にありません（「バリエーション」タブで足せます）"));
        for (int i = 0; i < doc.Sections.Count; i++)
            if (doc.Sections[i].VariationCond != null && doc.Variations.Count > 0 && !doc.VariationNames.Any(doc.Sections[i].InVariation))
                scene.Problems.Add(new(i, "どのバリエーションにも入っていません（書き出したパネルには入りません）"));
        foreach (var v in doc.Variations.Where(v => v.File.Trim().Length == 0))
            scene.Problems.Add(new(-1, $"バリエーション「{v.Name}」の出力ファイルが決まっていません"));
        foreach (var g in doc.Variations.Where(v => v.File.Trim().Length > 0).GroupBy(v => v.File.Trim().ToLowerInvariant()).Where(g => g.Count() > 1))
            scene.Problems.Add(new(-1, $"バリエーション「{string.Join("」「", g.Select(v => v.Name))}」の出力ファイルが同じです（{g.First().File}）"));
        // 描画順: Layer の小さい順、同じならファイルの順
        scene.Items = scene.Items.OrderBy(x => x.Layer).ThenBy(x => x.Section).ToList();
        return scene;
    }

    private static void ReadRange(PanelSection s, RenderItem item, Action<string> problem)
    {
        item.InitialAngle = PanelValues.Double(s.Get("InitialAngle"), 0);
        item.LastAngle = PanelValues.Double(s.Get("LastAngle"), 0);
        item.Minimum = PanelValues.Double(s.Get("Minimum"), 0);
        item.Maximum = PanelValues.Double(s.Get("Maximum"), 0);
        if (Math.Abs(item.Maximum - item.Minimum) < 1e-12) problem("Minimum と Maximum が同じ（または未設定）です");
    }
}
