namespace BvePanelEditor.Core.Psd;

public enum PsdEffectKind { DropShadow, OuterGlow, InnerShadow, InnerGlow, Bevel, Satin, ColorOverlay, GradientOverlay, PatternOverlay, Stroke }

/// <summary>グラデーション（色の点と不透明度の点）。位置は 0〜1。</summary>
public sealed class PsdGradient
{
    public List<(float Loc, float Mid, (float R, float G, float B) Color)> Colors = [];
    public List<(float Loc, float Mid, float Opacity)> Alphas = [];

    public (float R, float G, float B, float A) At(float t)
    {
        t = Math.Clamp(t, 0, 1);
        float r = 0, g = 0, b = 0, a = 1;
        if (Colors.Count > 0)
        {
            var (i, f) = Find(Colors.Select(c => (c.Loc, c.Mid)).ToList(), t);
            var c0 = Colors[i].Color; var c1 = Colors[Math.Min(i + 1, Colors.Count - 1)].Color;
            r = c0.R + (c1.R - c0.R) * f; g = c0.G + (c1.G - c0.G) * f; b = c0.B + (c1.B - c0.B) * f;
        }
        if (Alphas.Count > 0)
        {
            var (i, f) = Find(Alphas.Select(x => (x.Loc, x.Mid)).ToList(), t);
            a = Alphas[i].Opacity + (Alphas[Math.Min(i + 1, Alphas.Count - 1)].Opacity - Alphas[i].Opacity) * f;
        }
        return (r, g, b, a);
    }

    /// <summary>t がどの区間（i と i+1 の間）のどこ（0〜1）か。</summary>
    private static (int I, float F) Find(List<(float Loc, float Mid)> stops, float t)
    {
        if (t <= stops[0].Loc) return (0, 0);
        for (int i = 1; i < stops.Count; i++)
        {
            if (t > stops[i].Loc) continue;
            float span = stops[i].Loc - stops[i - 1].Loc;
            float f = span <= 0 ? 1 : (t - stops[i - 1].Loc) / span;
            // 中間点（Mdpn）: f = mid で色がちょうど半分になるようにゆがめる
            float mid = Math.Clamp(stops[i].Mid, 0.01f, 0.99f);
            f = f <= mid ? f / mid * 0.5f : 0.5f + (f - mid) / (1 - mid) * 0.5f;
            return (i - 1, f);
        }
        return (stops.Count - 1, 0);
    }
}

/// <summary>レイヤー効果 1 つ。色は 0〜1、大きさは px、角度は度。</summary>
public sealed class PsdEffect
{
    public PsdEffectKind Kind;
    public string Blend = "norm";
    public float Opacity = 1;
    public (float R, float G, float B) Color;
    public float Angle = 120, Altitude = 30, Distance, Size, Spread;
    public bool UseGlobalLight = true;
    public bool EdgeSource = true;               // 光彩（内側）: 境界から／中央から
    public string StrokePosition = "OutF";       // 境界線: 外側 OutF・内側 InsF・中央 CtrF
    public PsdGradient? Gradient;                // グラデーションオーバーレイ・グラデーションの境界線
    public string GradientType = "Lnr ";
    public bool Reverse, AlignWithLayer = true;
    public float GradientAngle = 90, GradientScale = 1, OffsetX, OffsetY;
    public string BevelStyle = "InrB";           // ベベルとエンボス: 内側 InrB・外側 OtrB・エンボス Embs・ピローエンボス PlEb・境界線エンボス strokeEmboss
    public float Depth = 1, Soften;
    public bool Chisel;                          // ベベル: テクニックが「シゼルハード／ソフト」
    public bool Up = true;
    public bool Invert;                          // サテン: 反転
    public (float R, float G, float B) ShadowColor;
    public string ShadowBlend = "mul ";
    public float ShadowOpacity = 0.75f;
}

/// <summary>レイヤーのレイヤー効果全部。</summary>
public sealed class PsdLayerEffects
{
    public List<PsdEffect> Items = [];
    /// <summary>再現しない効果・設定（一覧の注意書きと警告に出す）。</summary>
    public List<string> Unsupported = [];
    /// <summary>近似で再現しているもの。</summary>
    public List<string> Approximate = [];
    public bool HasRenderable => Items.Count > 0;

    /// <summary>効果が広がる幅（px）。表示灯の切り出し範囲を広げるのに使う。</summary>
    public int Margin()
    {
        float m = 0;
        foreach (var e in Items)
            m = Math.Max(m, e.Kind switch
            {
                PsdEffectKind.DropShadow => e.Distance + e.Size,
                PsdEffectKind.OuterGlow => e.Size,
                PsdEffectKind.Stroke => e.StrokePosition == "InsF" ? 0 : e.Size,
                PsdEffectKind.Bevel => e.BevelStyle is "OtrB" or "Embs" or "PlEb" ? e.Size : 0,
                _ => 0,
            });
        return m <= 0 ? 0 : (int)MathF.Ceiling(m) + 2;
    }
}

/// <summary>レイヤー効果を読む・描く。</summary>
public static class PsdEffects
{
    private static readonly Dictionary<string, PsdEffectKind> Keys = new()
    {
        ["DrSh"] = PsdEffectKind.DropShadow, ["dropShadowMulti"] = PsdEffectKind.DropShadow,
        ["IrSh"] = PsdEffectKind.InnerShadow, ["innerShadowMulti"] = PsdEffectKind.InnerShadow,
        ["OrGl"] = PsdEffectKind.OuterGlow, ["IrGl"] = PsdEffectKind.InnerGlow,
        ["ebbl"] = PsdEffectKind.Bevel, ["ChFX"] = PsdEffectKind.Satin,
        ["SoFi"] = PsdEffectKind.ColorOverlay, ["solidFillMulti"] = PsdEffectKind.ColorOverlay,
        ["GrFl"] = PsdEffectKind.GradientOverlay, ["gradientFillMulti"] = PsdEffectKind.GradientOverlay,
        ["patternFill"] = PsdEffectKind.PatternOverlay, ["patternFillMulti"] = PsdEffectKind.PatternOverlay,
        ["FrFX"] = PsdEffectKind.Stroke, ["frameFXMulti"] = PsdEffectKind.Stroke,
    };

    public static string Label(PsdEffectKind k) => k switch
    {
        PsdEffectKind.DropShadow => "ドロップシャドウ", PsdEffectKind.OuterGlow => "光彩（外側）", PsdEffectKind.InnerShadow => "シャドウ（内側）",
        PsdEffectKind.InnerGlow => "光彩（内側）", PsdEffectKind.Bevel => "ベベルとエンボス", PsdEffectKind.Satin => "サテン",
        PsdEffectKind.ColorOverlay => "カラーオーバーレイ", PsdEffectKind.GradientOverlay => "グラデーションオーバーレイ",
        PsdEffectKind.PatternOverlay => "パターンオーバーレイ", _ => "境界線",
    };

    /// <summary>描画モードの名前（記述子の enum）→ レイヤーの描画モードのキー。</summary>
    public static string BlendKey(string? name) => name switch
    {
        "Nrml" => "norm", "Dslv" => "diss", "Drkn" => "dark", "Mltp" => "mul ", "CBrn" => "idiv", "linearBurn" => "lbrn", "darkerColor" => "dkCl",
        "Lghn" => "lite", "Scrn" => "scrn", "CDdg" => "div ", "linearDodge" => "lddg", "lighterColor" => "lgCl",
        "Ovrl" => "over", "SftL" => "sLit", "HrdL" => "hLit", "vividLight" => "vLit", "linearLight" => "lLit", "pinLight" => "pLit", "hardMix" => "hMix",
        "Dfrn" => "diff", "Xclu" => "smud", "blendSubtraction" => "fsub", "blendDivide" => "fdiv",
        "H   " => "hue ", "Strt" => "sat ", "Clr " => "colr", "Lmns" => "lum ", _ => "norm",
    };

    // ================= 読む =================

    /// <summary>
    /// レイヤーの追加情報 lmfx（効果を複数重ねたもの）か lfx2 から読む。読めなければ null。
    /// globalAngle・globalAltitude は PSD 全体の光源（「包括光源を使用」の効果に使う）。
    /// </summary>
    public static PsdLayerEffects? Parse(byte[]? lfx2, byte[]? lmfx, float globalAngle, float globalAltitude)
    {
        var data = lmfx ?? lfx2;
        if (data == null || data.Length < 8) return null;
        if (PsdDescriptor.ReadVersioned(data[4..]) is not { } d) return null;
        var fx = new PsdLayerEffects();
        if (d.TryGetValue("masterFXSwitch", out var master) && master is false) return fx;
        float scale = (float)((PsdDescriptor.Number(d, "Scl ") ?? 100) / 100);
        foreach (var (key, value) in d)
        {
            if (!Keys.TryGetValue(key, out var kind)) continue;
            var list = value switch
            {
                Dictionary<string, object> one => [one],
                List<object> many => many.OfType<Dictionary<string, object>>().ToList(),
                _ => [],
            };
            foreach (var e in list)
            {
                if (e.TryGetValue("enab", out var en) && en is false) continue;
                if (e.TryGetValue("present", out var pr) && pr is false) continue;
                var eff = ReadEffect(kind, e, scale, globalAngle, globalAltitude, fx);
                if (eff != null) fx.Items.Add(eff);
            }
        }
        return fx;
    }

    private static PsdEffect? ReadEffect(PsdEffectKind kind, Dictionary<string, object> e, float scale, float gAngle, float gAlt, PsdLayerEffects fx)
    {
        string label = Label(kind);
        if (kind is PsdEffectKind.PatternOverlay)
        {
            fx.Unsupported.Add(label);
            return null;
        }
        float N(string k, double def) => (float)(PsdDescriptor.Number(e, k) ?? def);
        var eff = new PsdEffect
        {
            Kind = kind,
            Blend = BlendKey(e.TryGetValue("Md  ", out var md) ? md as string : null),
            Opacity = N("Opct", 100) / 100f,
            Color = ReadColor(e, "Clr ") ?? (0, 0, 0),
            UseGlobalLight = e.TryGetValue("uglg", out var ug) && ug is true,
            Angle = N("lagl", 120),
            Altitude = N("Lald", 30),
            Distance = N("Dstn", 0) * scale,
            Size = N("blur", 0) * scale,
            Spread = Math.Clamp(N("Ckmt", 0) / 100f, 0, 1),
        };
        if (eff.UseGlobalLight) { eff.Angle = gAngle; eff.Altitude = gAlt; }
        if (e.TryGetValue("TrnS", out var trns) && trns is Dictionary<string, object> contour && !IsLinearContour(contour))
            Note(fx.Approximate, $"{label}の輪郭は直線として扱います");
        if (e.TryGetValue("Nose", out _) && N("Nose", 0) > 0) Note(fx.Approximate, $"{label}のノイズは再現しません");

        switch (kind)
        {
            case PsdEffectKind.DropShadow:
            case PsdEffectKind.InnerShadow:
            case PsdEffectKind.OuterGlow:
                Note(fx.Approximate, $"{label}のぼかしは近似です");
                if (kind == PsdEffectKind.OuterGlow && e.TryGetValue("Grad", out var og) && og is Dictionary<string, object> ogd && ReadGradient(ogd) is { } ogg)
                    eff.Color = Rgb(ogg.At(0));
                break;
            case PsdEffectKind.InnerGlow:
                Note(fx.Approximate, $"{label}のぼかしは近似です");
                eff.EdgeSource = !(e.TryGetValue("glwS", out var src) && src is "SrcC");
                if (e.TryGetValue("Grad", out var ig) && ig is Dictionary<string, object> igd && ReadGradient(igd) is { } igg)
                    eff.Color = Rgb(igg.At(0));
                break;
            case PsdEffectKind.ColorOverlay:
                break;
            case PsdEffectKind.Satin:
                Note(fx.Approximate, "サテンは近似です");
                eff.Invert = e.TryGetValue("Invr", out var inv) && inv is true;
                break;
            case PsdEffectKind.GradientOverlay:
                if (!ReadGradientSettings(eff, e, fx, label)) return null;
                break;
            case PsdEffectKind.Stroke:
                eff.Size = N("Sz  ", 3) * scale;
                eff.StrokePosition = e.TryGetValue("Styl", out var st) && st is string s ? s : "OutF";
                var paint = e.TryGetValue("PntT", out var pt) ? pt as string : "SClr";
                if (paint == "Ptrn") { fx.Unsupported.Add("パターンの境界線"); return null; }
                if (paint == "GrFl" && !ReadGradientSettings(eff, e, fx, label)) return null;
                break;
            case PsdEffectKind.Bevel:
                Note(fx.Approximate, "ベベルとエンボスは近似です（陰影の付き方が Photoshop と違います）");
                eff.BevelStyle = e.TryGetValue("bvlS", out var bs) && bs is string b ? b : "InrB";
                if (eff.BevelStyle == "strokeEmboss") { Note(fx.Approximate, "境界線エンボスは内側のベベルとして扱います"); eff.BevelStyle = "InrB"; }
                eff.Depth = N("srgR", 100) / 100f;
                eff.Soften = N("Sftn", 0) * scale;
                eff.Chisel = e.TryGetValue("bvlT", out var tech) && tech is "PrBL" or "Slmt";
                eff.Up = !(e.TryGetValue("bvlD", out var dir) && dir is "Out ");
                eff.Blend = BlendKey(e.TryGetValue("hglM", out var hm) ? hm as string : "Scrn");
                eff.Color = ReadColor(e, "hglC") ?? (1, 1, 1);
                eff.Opacity = N("hglO", 75) / 100f;
                eff.ShadowBlend = BlendKey(e.TryGetValue("sdwM", out var sm) ? sm as string : "Mltp");
                eff.ShadowColor = ReadColor(e, "sdwC") ?? (0, 0, 0);
                eff.ShadowOpacity = N("sdwO", 75) / 100f;
                break;
        }
        return eff;
    }

    private static void Note(List<string> list, string text) { if (!list.Contains(text)) list.Add(text); }

    private static (float, float, float) Rgb((float R, float G, float B, float A) c) => (c.R, c.G, c.B);

    private static bool ReadGradientSettings(PsdEffect eff, Dictionary<string, object> e, PsdLayerEffects fx, string label)
    {
        if (!e.TryGetValue("Grad", out var g) || g is not Dictionary<string, object> gd || ReadGradient(gd) is not { } grad)
        {
            fx.Unsupported.Add($"{label}（ノイズのグラデーション）");
            return false;
        }
        eff.Gradient = grad;
        eff.GradientAngle = (float)(PsdDescriptor.Number(e, "Angl") ?? 90);
        eff.GradientType = e.TryGetValue("Type", out var t) && t is string ts ? ts : "Lnr ";
        eff.Reverse = e.TryGetValue("Rvrs", out var r) && r is true;
        eff.AlignWithLayer = !e.TryGetValue("Algn", out var a) || a is true;
        eff.GradientScale = (float)((PsdDescriptor.Number(e, "Scl ") ?? 100) / 100);
        if (e.TryGetValue("Ofst", out var o) && o is Dictionary<string, object> od)
        {
            eff.OffsetX = (float)((PsdDescriptor.Number(od, "Hrzn") ?? 0) / 100);
            eff.OffsetY = (float)((PsdDescriptor.Number(od, "Vrtc") ?? 0) / 100);
        }
        Note(fx.Approximate, $"{label}の範囲は近似です");
        return true;
    }

    /// <summary>グラデーション（カスタムのみ。ノイズは null）。</summary>
    public static PsdGradient? ReadGradient(Dictionary<string, object> g)
    {
        if (g.TryGetValue("GrdF", out var f) && f is "ClNs") return null;
        var grad = new PsdGradient();
        if (g.TryGetValue("Clrs", out var cl) && cl is List<object> colors)
            foreach (var c in colors.OfType<Dictionary<string, object>>())
            {
                var type = c.TryGetValue("Type", out var ty) ? ty as string : "UsrS";
                var color = type == "BckC" ? (1f, 1f, 1f) : type == "FrgC" ? (0f, 0f, 0f) : ReadColor(c, "Clr ") ?? (0, 0, 0);
                grad.Colors.Add(((float)((PsdDescriptor.Number(c, "Lctn") ?? 0) / 4096), (float)((PsdDescriptor.Number(c, "Mdpn") ?? 50) / 100), color));
            }
        if (g.TryGetValue("Trns", out var tr) && tr is List<object> alphas)
            foreach (var a in alphas.OfType<Dictionary<string, object>>())
                grad.Alphas.Add(((float)((PsdDescriptor.Number(a, "Lctn") ?? 0) / 4096), (float)((PsdDescriptor.Number(a, "Mdpn") ?? 50) / 100),
                    (float)((PsdDescriptor.Number(a, "Opct") ?? 100) / 100)));
        grad.Colors.Sort((a, b) => a.Loc.CompareTo(b.Loc));
        grad.Alphas.Sort((a, b) => a.Loc.CompareTo(b.Loc));
        return grad.Colors.Count == 0 ? null : grad;
    }

    /// <summary>色（RGB・HSB・グレー）。読めなければ null。</summary>
    public static (float R, float G, float B)? ReadColor(Dictionary<string, object> d, string key)
    {
        if (!d.TryGetValue(key, out var v) || v is not Dictionary<string, object> c) return null;
        if (PsdDescriptor.Number(c, "Rd  ") is { } r && PsdDescriptor.Number(c, "Grn ") is { } g && PsdDescriptor.Number(c, "Bl  ") is { } b)
            return ((float)(r / 255), (float)(g / 255), (float)(b / 255));
        if (PsdDescriptor.Number(c, "H   ") is { } h && PsdDescriptor.Number(c, "Strt") is { } s && PsdDescriptor.Number(c, "Brgh") is { } br)
        {
            float hh = (float)(h / 360 % 1), ss = (float)(s / 100), vv = (float)(br / 100);
            int i = (int)(hh * 6); float f = hh * 6 - i, p = vv * (1 - ss), q = vv * (1 - f * ss), t = vv * (1 - (1 - f) * ss);
            return (i % 6) switch { 0 => (vv, t, p), 1 => (q, vv, p), 2 => (p, vv, t), 3 => (p, q, vv), 4 => (t, p, vv), _ => (vv, p, q) };
        }
        if (PsdDescriptor.Number(c, "Gry ") is { } gray) { float x = (float)(1 - gray / 100); return (x, x, x); }
        return null;
    }

    private static bool IsLinearContour(Dictionary<string, object> c)
    {
        if (!c.TryGetValue("Crv ", out var v) || v is not List<object> pts) return true;
        foreach (var p in pts.OfType<Dictionary<string, object>>())
        {
            double x = PsdDescriptor.Number(p, "Hrzn") ?? 0, y = PsdDescriptor.Number(p, "Vrtc") ?? 0;
            if (Math.Abs(x - y) > 1) return false;
        }
        return true;
    }

    /// <summary>効果を含めた見た目の範囲（効果が広がる分だけ広げる）。</summary>
    public static PsdRect VisualBounds(PsdLayer l)
    {
        int m = l.Effects is { HasRenderable: true } fx ? fx.Margin() : 0;
        var b = l.Bounds;
        return m == 0 || b.IsEmpty ? b : new PsdRect(b.Left - m, b.Top - m, b.Right + m, b.Bottom + m);
    }

    /// <summary>レイヤー効果について、重ねる・書き出すときの注意（再現しないもの・近似のもの）。</summary>
    public static IEnumerable<string> Warnings(PsdLayer l)
    {
        if (!l.HasEffects) yield break;
        if (l.IsFolder) { yield return $"「{l.Path}」はフォルダのレイヤー効果なので再現しません。Photoshop でラスタライズしてください"; yield break; }
        if (l.Effects == null) { yield return $"「{l.Path}」のレイヤー効果は読めない形式なので再現しません。Photoshop でラスタライズしてください"; yield break; }
        if (l.Effects.Unsupported.Count > 0)
            yield return $"「{l.Path}」の{string.Join("・", l.Effects.Unsupported.Distinct())}は再現しません。Photoshop でラスタライズしてください";
        foreach (var a in l.Effects.Approximate) yield return $"「{l.Path}」: {a}";
    }

    /// <summary>一覧に出す短い説明（使っている効果）。</summary>
    public static string Summary(PsdLayer l)
    {
        if (!l.HasEffects) return "";
        if (l.IsFolder) return "フォルダの効果（再現しません）";
        if (l.Effects == null) return "効果（読めない形式・再現しません）";
        var names = l.Effects.Items.Select(e => Label(e.Kind)).Distinct().ToList();
        var s = names.Count > 0 ? "効果: " + string.Join("・", names) : "効果（すべてオフ）";
        if (l.Effects.Unsupported.Count > 0) s += $"（{string.Join("・", l.Effects.Unsupported.Distinct())}は再現しません）";
        return s;
    }

    // ================= 描く =================

    /// <summary>描いた効果 1 枚（キャンバス座標の範囲、色はそのまま、アルファ込み）。Above なら本体の上、でなければ下に重ねる。</summary>
    public sealed record Part(PsdPixels Pixels, string Blend, float Opacity, bool Above);

    /// <summary>レイヤーの中身（マスク適用済み）から、効果の絵を作る。下に重ねるもの → 上に重ねるもの の順。</summary>
    public static List<Part> Render(PsdLayerEffects fx, PsdPixels content, PsdRect canvas)
    {
        var parts = new List<Part>();
        if (!fx.HasRenderable || content.Width == 0) return parts;
        int margin = fx.Margin() + 2; // 内側の効果でも「形の外」が格子に入るように余白を取る
        var b = content.Bounds;
        var region = new PsdRect(b.Left - margin, b.Top - margin, b.Right + margin, b.Bottom + margin).Intersect(
            new PsdRect(canvas.Left - margin, canvas.Top - margin, canvas.Right + margin, canvas.Bottom + margin));
        if (region.IsEmpty) return parts;
        var f = new Field(region, content);

        // Photoshop の重なり順（下から）: ドロップシャドウ、光彩（外側）、[本体]、シャドウ（内側）、光彩（内側）、ベベル、サテン、カラー、グラデーション、境界線
        foreach (var e in fx.Items.Where(e => e.Kind == PsdEffectKind.DropShadow).Reverse()) parts.Add(DropShadow(f, e));
        foreach (var e in fx.Items.Where(e => e.Kind == PsdEffectKind.OuterGlow)) parts.Add(Solid(f, OuterGlowShape(f, e), e.Color, e, false));
        foreach (var e in fx.Items.Where(e => e.Kind == PsdEffectKind.InnerShadow).Reverse()) parts.Add(Solid(f, InnerShadowShape(f, e), e.Color, e, true));
        foreach (var e in fx.Items.Where(e => e.Kind == PsdEffectKind.InnerGlow)) parts.Add(Solid(f, InnerGlowShape(f, e), e.Color, e, true));
        foreach (var e in fx.Items.Where(e => e.Kind == PsdEffectKind.Bevel)) parts.AddRange(Bevel(f, e));
        foreach (var e in fx.Items.Where(e => e.Kind == PsdEffectKind.Satin)) parts.Add(Solid(f, SatinShape(f, e), e.Color, e, true));
        foreach (var e in fx.Items.Where(e => e.Kind == PsdEffectKind.ColorOverlay).Reverse()) parts.Add(Solid(f, f.A, e.Color, e, true));
        foreach (var e in fx.Items.Where(e => e.Kind == PsdEffectKind.GradientOverlay).Reverse()) parts.Add(Gradient(f, f.A, e, b, canvas));
        foreach (var e in fx.Items.Where(e => e.Kind == PsdEffectKind.Stroke).Reverse())
        {
            var shape = StrokeShape(f, e);
            parts.Add(e.Gradient != null ? Gradient(f, shape, e, b, canvas) : Solid(f, shape, e.Color, e, true));
        }
        return parts;
    }

    /// <summary>作業用の格子（中身のアルファと、境界までの距離）。</summary>
    private sealed class Field
    {
        public readonly PsdRect R;
        public readonly int W, H;
        public readonly float[] A;
        private float[]? _dOut, _dIn;

        public Field(PsdRect region, PsdPixels content)
        {
            R = region; W = region.Width; H = region.Height;
            A = new float[W * H];
            var cb = content.Bounds.Intersect(region);
            for (int y = cb.Top; y < cb.Bottom; y++)
                for (int x = cb.Left; x < cb.Right; x++)
                    A[(y - R.Top) * W + (x - R.Left)] = content.Bgra[((y - content.Bounds.Top) * content.Width + (x - content.Bounds.Left)) * 4 + 3] / 255f;
        }

        /// <summary>形の外側の点から、形（アルファ 0.5 以上）までの距離。形の中は 0。</summary>
        public float[] DOut => _dOut ??= Distance(W, H, i => A[i] >= 0.5f);
        /// <summary>形の中の点から、形の外までの距離。形の外は 0。</summary>
        public float[] DIn => _dIn ??= Distance(W, H, i => A[i] < 0.5f);

        public float[] Dilate(float r)
        {
            var o = (float[])A.Clone();
            if (r <= 0) return o;
            var d = DOut;
            for (int i = 0; i < o.Length; i++) o[i] = MathF.Max(o[i], Math.Clamp(r + 0.5f - d[i], 0, 1));
            return o;
        }

        public float[] Erode(float r)
        {
            var o = (float[])A.Clone();
            if (r <= 0) return o;
            var d = DIn;
            for (int i = 0; i < o.Length; i++) o[i] = MathF.Min(o[i], Math.Clamp(d[i] - r, 0, 1));
            return o;
        }
    }

    private static Part DropShadow(Field f, PsdEffect e)
    {
        var s = f.Dilate(e.Spread * e.Size);
        Blur(s, f.W, f.H, e.Size * (1 - e.Spread), 0);
        double rad = e.Angle * Math.PI / 180;
        int dx = (int)Math.Round(-Math.Cos(rad) * e.Distance), dy = (int)Math.Round(Math.Sin(rad) * e.Distance);
        s = Shift(s, f.W, f.H, dx, dy, 0);
        return Solid(f, s, e.Color, e, false);
    }

    private static float[] OuterGlowShape(Field f, PsdEffect e)
    {
        var s = f.Dilate(e.Spread * e.Size);
        Blur(s, f.W, f.H, e.Size * (1 - e.Spread), 0);
        return s;
    }

    private static float[] InnerShadowShape(Field f, PsdEffect e)
    {
        // 外側（1 − 形）を光と反対へずらし、チョークの分だけ広げてぼかし、形の中だけ残す
        var o = f.Erode(e.Spread * e.Size);
        for (int i = 0; i < o.Length; i++) o[i] = 1 - o[i];
        double rad = e.Angle * Math.PI / 180;
        int dx = (int)Math.Round(-Math.Cos(rad) * e.Distance), dy = (int)Math.Round(Math.Sin(rad) * e.Distance);
        o = Shift(o, f.W, f.H, dx, dy, 1);
        Blur(o, f.W, f.H, e.Size * (1 - e.Spread), 1);
        for (int i = 0; i < o.Length; i++) o[i] *= f.A[i];
        return o;
    }

    private static float[] InnerGlowShape(Field f, PsdEffect e)
    {
        var o = f.Erode(e.Spread * e.Size);
        for (int i = 0; i < o.Length; i++) o[i] = 1 - o[i];
        Blur(o, f.W, f.H, e.Size * (1 - e.Spread), 1);
        for (int i = 0; i < o.Length; i++) o[i] = (e.EdgeSource ? o[i] : 1 - o[i]) * f.A[i];
        return o;
    }

    /// <summary>サテン: 形を角度の向きへ前後にずらしてぼかし、その差を形の中だけ残す。</summary>
    private static float[] SatinShape(Field f, PsdEffect e)
    {
        double rad = e.Angle * Math.PI / 180;
        int dx = (int)Math.Round(Math.Cos(rad) * e.Distance), dy = (int)Math.Round(-Math.Sin(rad) * e.Distance);
        var a = Shift(f.A, f.W, f.H, dx, dy, 0);
        var b = Shift(f.A, f.W, f.H, -dx, -dy, 0);
        if (ReferenceEquals(a, f.A)) a = (float[])a.Clone();
        if (ReferenceEquals(b, f.A)) b = (float[])b.Clone();
        Blur(a, f.W, f.H, e.Size, 0);
        Blur(b, f.W, f.H, e.Size, 0);
        var o = new float[f.A.Length];
        for (int i = 0; i < o.Length; i++)
        {
            float v = MathF.Abs(a[i] - b[i]);
            o[i] = (e.Invert ? 1 - v : v) * f.A[i];
        }
        return o;
    }

    private static float[] StrokeShape(Field f, PsdEffect e)
    {
        float[] outer, inner;
        switch (e.StrokePosition)
        {
            case "InsF": outer = f.A; inner = f.Erode(e.Size); break;
            case "CtrF": outer = f.Dilate(e.Size / 2); inner = f.Erode(e.Size / 2); break;
            default: outer = f.Dilate(e.Size); inner = f.A; break;
        }
        var s = new float[f.A.Length];
        for (int i = 0; i < s.Length; i++) s[i] = MathF.Max(0, outer[i] - inner[i]);
        return s;
    }

    private static IEnumerable<Part> Bevel(Field f, PsdEffect e)
    {
        float size = Math.Max(1, e.Size);
        var dIn = f.DIn; var dOut = f.DOut;
        var h = new float[f.A.Length];
        var area = new float[f.A.Length];
        for (int i = 0; i < h.Length; i++)
        {
            float inside = Math.Clamp(dIn[i] / size, 0, 1), outside = Math.Clamp(dOut[i] / size, 0, 1);
            bool isIn = f.A[i] >= 0.5f;
            switch (e.BevelStyle)
            {
                case "OtrB": h[i] = isIn ? 1 : 1 - outside; area[i] = isIn ? 0 : (dOut[i] <= size ? 1 - f.A[i] : 0); break;
                case "Embs": h[i] = isIn ? 0.5f + 0.5f * inside : 0.5f - 0.5f * outside; area[i] = isIn ? f.A[i] : (dOut[i] <= size ? 1 : 0); break;
                case "PlEb": h[i] = isIn ? inside : outside; area[i] = isIn ? f.A[i] : (dOut[i] <= size ? 1 : 0); break;
                default: h[i] = inside; area[i] = f.A[i]; break;
            }
        }
        // 「滑らかに」は角が出ないよう高さを大きめにならす（シゼルは少しだけ）
        float smooth = e.Soften + (e.Chisel ? 1 : size / 3);
        Blur(h, f.W, f.H, smooth, 0);
        double ang = e.Angle * Math.PI / 180, alt = e.Altitude * Math.PI / 180;
        float lx = (float)(Math.Cos(alt) * Math.Cos(ang)), ly = (float)(-Math.Cos(alt) * Math.Sin(ang)), lz = (float)Math.Sin(alt);
        float k = size * e.Depth * (e.Up ? 1 : -1);
        var hi = new float[h.Length]; var sh = new float[h.Length];
        for (int y = 0; y < f.H; y++)
            for (int x = 0; x < f.W; x++)
            {
                int i = y * f.W + x;
                if (area[i] <= 0) continue;
                float gx = (h[y * f.W + Math.Min(f.W - 1, x + 1)] - h[y * f.W + Math.Max(0, x - 1)]) / 2 * k;
                float gy = (h[Math.Min(f.H - 1, y + 1) * f.W + x] - h[Math.Max(0, y - 1) * f.W + x]) / 2 * k;
                float len = MathF.Sqrt(gx * gx + gy * gy + 1);
                float shade = (-gx * lx - gy * ly + lz) / len - lz;
                if (shade > 0) hi[i] = Math.Clamp(shade * 2, 0, 1) * area[i];
                else sh[i] = Math.Clamp(-shade * 2, 0, 1) * area[i];
            }
        yield return new Part(ToPixels(f, sh, (_, _) => (e.ShadowColor.R, e.ShadowColor.G, e.ShadowColor.B, 1)), e.ShadowBlend, e.ShadowOpacity, true);
        yield return new Part(ToPixels(f, hi, (_, _) => (e.Color.R, e.Color.G, e.Color.B, 1)), e.Blend, e.Opacity, true);
    }

    private static Part Solid(Field f, float[] shape, (float R, float G, float B) c, PsdEffect e, bool above) =>
        new(ToPixels(f, shape, (_, _) => (c.R, c.G, c.B, 1)), e.Blend, e.Opacity, above);

    /// <summary>グラデーションで塗る。基準の枠はレイヤーの範囲（「レイヤーに揃える」がオフならキャンバス）。</summary>
    private static Part Gradient(Field f, float[] shape, PsdEffect e, PsdRect layerBounds, PsdRect canvas)
    {
        var box = e.AlignWithLayer ? layerBounds : canvas;
        float cx = box.Left + box.Width / 2f + e.OffsetX * box.Width, cy = box.Top + box.Height / 2f + e.OffsetY * box.Height;
        double rad = e.GradientAngle * Math.PI / 180;
        float ux = (float)Math.Cos(rad), uy = (float)-Math.Sin(rad);
        float len = (MathF.Abs(box.Width * ux) + MathF.Abs(box.Height * uy)) * e.GradientScale;
        float radius = MathF.Sqrt(box.Width * box.Width + box.Height * box.Height) / 2 * e.GradientScale;
        len = Math.Max(1, len); radius = Math.Max(1, radius);
        var g = e.Gradient!;
        return new Part(ToPixels(f, shape, (x, y) =>
        {
            float px = x + 0.5f - cx, py = y + 0.5f - cy;
            float along = px * ux + py * uy, across = -px * uy + py * ux;
            float t = e.GradientType switch
            {
                "Rdl " => MathF.Sqrt(px * px + py * py) / radius,
                "Rflc" => MathF.Abs(along) / (len / 2),
                "Dmnd" => (MathF.Abs(along) + MathF.Abs(across)) / radius,
                "Angl" => (float)((Math.Atan2(-py, px) - rad) / (2 * Math.PI) % 1 + 1) % 1,
                _ => along / len + 0.5f,
            };
            if (e.Reverse) t = 1 - t;
            return g.At(t);
        }), e.Blend, e.Opacity, true);
    }

    private static PsdPixels ToPixels(Field f, float[] shape, Func<int, int, (float R, float G, float B, float A)> color)
    {
        var bgra = new byte[f.W * f.H * 4];
        for (int y = 0; y < f.H; y++)
            for (int x = 0; x < f.W; x++)
            {
                int i = y * f.W + x;
                float a = shape[i];
                if (a <= 0) continue;
                var (r, g, b, ca) = color(f.R.Left + x, f.R.Top + y);
                bgra[i * 4] = B(b); bgra[i * 4 + 1] = B(g); bgra[i * 4 + 2] = B(r); bgra[i * 4 + 3] = B(a * ca);
            }
        return new PsdPixels(f.R, bgra);
    }

    private static byte B(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255), 0, 255);

    // ================= 道具 =================

    /// <summary>ずらす（はみ出した所は fill）。</summary>
    private static float[] Shift(float[] s, int w, int h, int dx, int dy, float fill)
    {
        if (dx == 0 && dy == 0) return s;
        var o = new float[s.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int sx = x - dx, sy = y - dy;
                o[y * w + x] = sx >= 0 && sy >= 0 && sx < w && sy < h ? s[sy * w + sx] : fill;
            }
        return o;
    }

    /// <summary>ぼかし（箱型を 3 回でガウスに近づける。size は広がる幅の px）。範囲の外は edge の値とみなす。</summary>
    public static void Blur(float[] s, int w, int h, float size, float edge)
    {
        if (size < 1) return;
        // 3 回の箱型の広がり ≒ 3 × 半径 なので、半径 = 幅 ÷ 3 くらい
        int r = Math.Max(1, (int)MathF.Round(size / 3));
        var tmp = new float[Math.Max(w, h)];
        for (int pass = 0; pass < 3; pass++)
        {
            for (int y = 0; y < h; y++) BoxLine(s, y * w, 1, w, r, edge, tmp);
            for (int x = 0; x < w; x++) BoxLine(s, x, w, h, r, edge, tmp);
        }
    }

    private static void BoxLine(float[] s, int start, int step, int n, int r, float edge, float[] tmp)
    {
        float Get(int i) => i < 0 || i >= n ? edge : s[start + i * step];
        float sum = 0;
        for (int i = -r; i <= r; i++) sum += Get(i);
        float inv = 1f / (2 * r + 1);
        for (int i = 0; i < n; i++)
        {
            tmp[i] = sum * inv;
            sum += Get(i + r + 1) - Get(i - r);
        }
        for (int i = 0; i < n; i++) s[start + i * step] = tmp[i];
    }

    /// <summary>ユークリッド距離（target の点までの距離。target の点は 0）。Felzenszwalb の方法。</summary>
    private static float[] Distance(int w, int h, Func<int, bool> target)
    {
        const float Inf = 1e20f;
        var g = new float[w * h];
        for (int i = 0; i < g.Length; i++) g[i] = target(i) ? 0 : Inf;
        var f = new float[Math.Max(w, h)];
        var d = new float[Math.Max(w, h)];
        var v = new int[Math.Max(w, h)];
        var z = new float[Math.Max(w, h) + 1];
        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++) f[y] = g[y * w + x];
            Edt1(f, h, d, v, z);
            for (int y = 0; y < h; y++) g[y * w + x] = d[y];
        }
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++) f[x] = g[y * w + x];
            Edt1(f, w, d, v, z);
            for (int x = 0; x < w; x++) g[y * w + x] = MathF.Sqrt(d[x]);
        }
        return g;
    }

    private static void Edt1(float[] f, int n, float[] d, int[] v, float[] z)
    {
        int k = 0;
        v[0] = 0; z[0] = float.NegativeInfinity; z[1] = float.PositiveInfinity;
        for (int q = 1; q < n; q++)
        {
            float s;
            while (true)
            {
                int p = v[k];
                s = ((f[q] + q * q) - (f[p] + p * p)) / (2f * q - 2f * p);
                if (s > z[k] || k == 0) break; // z[0] は −∞ なので k は 0 より下がらない
                k--;
            }
            k++;
            v[k] = q; z[k] = s; z[k + 1] = float.PositiveInfinity;
        }
        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            int p = v[k];
            d[q] = (q - p) * (q - p) + f[p];
        }
    }
}
