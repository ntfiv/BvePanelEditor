namespace BvePanelEditor.Core.Psd;

/// <summary>PSD のレイヤーを重ねる。</summary>
public static class PsdCompose
{
    /// <summary>描画モードのキーと日本語名。ここにあるものは全部再現する（ディザ合成だけは通常として重ねる）。</summary>
    public static readonly IReadOnlyDictionary<string, string> BlendNames = new Dictionary<string, string>
    {
        ["norm"] = "通常", ["pass"] = "通過", ["diss"] = "ディザ合成",
        ["dark"] = "比較（暗）", ["mul "] = "乗算", ["idiv"] = "焼き込みカラー", ["lbrn"] = "焼き込み（リニア）", ["dkCl"] = "カラー比較（暗）",
        ["lite"] = "比較（明）", ["scrn"] = "スクリーン", ["div "] = "覆い焼きカラー", ["lddg"] = "覆い焼き（リニア）- 加算", ["lgCl"] = "カラー比較（明）",
        ["over"] = "オーバーレイ", ["sLit"] = "ソフトライト", ["hLit"] = "ハードライト", ["vLit"] = "ビビッドライト", ["lLit"] = "リニアライト",
        ["pLit"] = "ピンライト", ["hMix"] = "ハードミックス",
        ["diff"] = "差の絶対値", ["smud"] = "除外", ["fsub"] = "減算", ["fdiv"] = "除算",
        ["hue "] = "色相", ["sat "] = "彩度", ["colr"] = "カラー", ["lum "] = "輝度",
    };

    public static bool IsSupportedBlend(string key) => BlendNames.ContainsKey(key);

    public static bool IsNormalBlend(string key) => key is "norm" or "pass" or "diss";

    /// <summary>描画モードの日本語名（知らないものはキーのまま）。</summary>
    public static string BlendLabel(string key) => BlendNames.TryGetValue(key, out var n) ? n : key.Trim();

    /// <summary>重ねている途中の絵（乗算済みの色とアルファ、0〜1）。</summary>
    private sealed class Accum
    {
        public readonly PsdRect Area;
        public readonly int Scale, W, H;
        public readonly float[] R, G, B, A;

        public Accum(PsdRect area, int scale)
        {
            Area = area;
            Scale = Math.Max(1, scale);
            W = (area.Width + Scale - 1) / Scale;
            H = (area.Height + Scale - 1) / Scale;
            R = new float[W * H]; G = new float[W * H]; B = new float[W * H]; A = new float[W * H];
        }

        public PsdPixels ToPixels()
        {
            var bgra = new byte[W * H * 4];
            for (int i = 0; i < W * H; i++)
            {
                float a = A[i];
                if (a <= 0) continue;
                bgra[i * 4] = ToByte(B[i] / a);
                bgra[i * 4 + 1] = ToByte(G[i] / a);
                bgra[i * 4 + 2] = ToByte(R[i] / a);
                bgra[i * 4 + 3] = ToByte(a);
            }
            var rect = Scale == 1 ? Area : new PsdRect(0, 0, W, H);
            return new PsdPixels(rect, bgra);
        }
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    /// <summary>
    /// レイヤーを下から順に重ねる（表示・非表示は見ない。渡したものを全部重ねる）。
    /// 画像レイヤーのほか、再現できる調整レイヤー・べた塗りレイヤーも重ねる。
    /// scale が 2 以上なら縮小して重ねる（見本用、戻り値の Bounds は 0,0 起点）。
    /// </summary>
    public static PsdPixels Compose(PsdFile psd, IEnumerable<PsdLayer> layers, PsdRect area, int scale = 1,
        ICollection<string>? warnings = null, CancellationToken ct = default)
    {
        var acc = new Accum(area, scale);
        var clips = new Dictionary<PsdLayer, PsdPixels?>();
        foreach (var l in layers.Where(l => l.CanCompose).OrderBy(l => l.Index))
        {
            ct.ThrowIfCancellationRequested();
            if (l.BlendMode == "diss" && l.Kind != PsdLayerKind.Adjustment)
                warnings?.Add($"「{l.Path}」の描画モード（ディザ合成）は通常として重ねました");
            else if (!IsSupportedBlend(l.BlendMode))
                warnings?.Add($"「{l.Path}」の描画モード（{BlendLabel(l.BlendMode)}）は通常として重ねました");
            if (l.Adjustment?.Note is { } note) warnings?.Add($"「{l.Path}」: {note}");
            if (warnings != null) foreach (var w in PsdEffects.Warnings(l)) warnings.Add(w);
            var clipBase = l.Clipped ? ClipBaseOf(psd, l) : null;
            Draw(psd, acc, l, l.BlendMode, clipBase is null ? null : ClipShape(psd, clipBase, clips));
        }
        return acc.ToPixels();
    }

    /// <summary>
    /// 1 つのレイヤーを area の範囲で切り出す。そのレイヤーにクリッピングされた上のレイヤーも重ねる。
    /// 自分の描画モードは通常として扱う（BVE は通常で描くため）。
    /// </summary>
    public static PsdPixels RenderLayer(PsdFile psd, PsdLayer layer, PsdRect area, ICollection<string>? warnings = null)
    {
        var acc = new Accum(area, 1);
        if (!layer.HasPixels) return acc.ToPixels();
        var own = psd.GetPixels(layer);
        var clips = new Dictionary<PsdLayer, PsdPixels?>();
        if (warnings != null) foreach (var w in PsdEffects.Warnings(layer)) warnings.Add(w);
        // 自分がクリッピングされているなら、土台の形で切り抜く（Photoshop での見た目と同じにする）
        var ownClip = layer.Clipped && ClipBaseOf(psd, layer) is { } clipBase ? ClipShape(psd, clipBase, clips) : null;
        Draw(psd, acc, layer, "norm", ownClip);
        foreach (var c in ClippedOnto(psd, layer))
        {
            if (!IsSupportedBlend(c.BlendMode))
                warnings?.Add($"「{c.Path}」の描画モード（{BlendLabel(c.BlendMode)}）は通常として重ねました");
            Draw(psd, acc, c, c.BlendMode, own);
        }
        return acc.ToPixels();
    }

    /// <summary>
    /// クリッピングの土台になっているレイヤー（すぐ下の、クリッピングされていないレイヤー。すぐ下がフォルダならそのフォルダ）。
    /// フォルダの中のいちばん下なら土台はない。
    /// </summary>
    public static PsdLayer? ClipBaseOf(PsdFile psd, PsdLayer layer)
    {
        for (int i = layer.Index - 1; i >= 0; i--)
        {
            var l = psd.Layers[i];
            if (l.Kind == PsdLayerKind.FolderEnd) return null;
            if (l.Kind == PsdLayerKind.FolderOpen) return l.Clipped ? null : l;
            if (!l.Clipped) return l;
        }
        return null;
    }

    /// <summary>クリッピングの土台の形（アルファだけを使う）。フォルダなら、中で表示されているものを重ねた形。</summary>
    private static PsdPixels? ClipShape(PsdFile psd, PsdLayer clipBase, Dictionary<PsdLayer, PsdPixels?> cache)
    {
        if (cache.TryGetValue(clipBase, out var shape)) return shape;
        if (!clipBase.IsFolder) shape = clipBase.Kind == PsdLayerKind.Fill ? FillPixels(psd, clipBase) : psd.GetPixels(clipBase);
        else
        {
            var inner = psd.ComposableLayers.Where(l => IsVisibleInside(l, clipBase)).ToList();
            var acc = new Accum(psd.Canvas, 1);
            foreach (var l in inner.OrderBy(l => l.Index))
            {
                var cb = l.Clipped ? ClipBaseOf(psd, l) : null;
                Draw(psd, acc, l, l.BlendMode, cb is null ? null : ClipShape(psd, cb, cache), stopAt: clipBase);
            }
            // フォルダ自身のマスクも形に含める
            shape = acc.ToPixels();
            if (psd.GetMask(clipBase) is { } m) ApplyMaskTo(shape, m);
        }
        cache[clipBase] = shape;
        return shape;
    }

    /// <summary>folder の中にあり、folder までの間（自分を含む）に非表示がないか。</summary>
    private static bool IsVisibleInside(PsdLayer l, PsdLayer folder)
    {
        if (l.Hidden) return false;
        for (var p = l.Parent; p != null; p = p.Parent)
        {
            if (p == folder) return true;
            if (p.Hidden) return false;
        }
        return false;
    }

    private static void ApplyMaskTo(PsdPixels px, PsdMask m)
    {
        for (int y = 0; y < px.Height; y++)
            for (int x = 0; x < px.Width; x++)
            {
                int a = (y * px.Width + x) * 4 + 3;
                px.Bgra[a] = (byte)(px.Bgra[a] * m.At(px.Bounds.Left + x, px.Bounds.Top + y));
            }
    }

    /// <summary>このレイヤーにクリッピングされている上のレイヤー。</summary>
    public static IEnumerable<PsdLayer> ClippedOnto(PsdFile psd, PsdLayer layer)
    {
        for (int i = layer.Index + 1; i < psd.Layers.Count; i++)
        {
            var l = psd.Layers[i];
            if (!l.Clipped || l.Kind is PsdLayerKind.FolderEnd or PsdLayerKind.FolderOpen) yield break;
            if (l.CanCompose && !l.Hidden) yield return l;
        }
    }

    /// <summary>フォルダの不透明度も含めた不透明度（0〜1）。</summary>
    private static float OpacityOf(PsdLayer l, PsdLayer? stopAt)
    {
        float o = l.Opacity / 255f * (l.FillOpacity / 255f);
        for (var p = l.Parent; p != null && p != stopAt; p = p.Parent) o *= p.Opacity / 255f;
        return o;
    }

    /// <summary>親のフォルダのマスク（stopAt より内側だけ）。</summary>
    private static List<PsdMask> FolderMasks(PsdFile psd, PsdLayer l, PsdLayer? stopAt)
    {
        var list = new List<PsdMask>();
        for (var p = l.Parent; p != null && p != stopAt; p = p.Parent)
            if (psd.GetMask(p) is { } m) list.Add(m);
        return list;
    }

    /// <summary>べた塗りレイヤーをキャンバス大の絵にする（マスクで形を付ける）。</summary>
    private static PsdPixels? FillPixels(PsdFile psd, PsdLayer layer)
    {
        if (layer.SolidColor is not { } c) return null;
        var mask = psd.GetMask(layer);
        // マスクの外が 0 なら、マスクの範囲だけでよい
        var rect = mask != null && mask.Outside == 0 ? mask.Bounds.Intersect(psd.Canvas) : psd.Canvas;
        if (rect.IsEmpty) return null;
        var bgra = new byte[rect.Width * rect.Height * 4];
        for (int y = 0; y < rect.Height; y++)
            for (int x = 0; x < rect.Width; x++)
            {
                int i = (y * rect.Width + x) * 4;
                bgra[i] = c.B; bgra[i + 1] = c.G; bgra[i + 2] = c.R;
                bgra[i + 3] = mask == null ? (byte)255 : ToByte(mask.At(rect.Left + x, rect.Top + y));
            }
        return new PsdPixels(rect, bgra);
    }

    private static void Draw(PsdFile psd, Accum acc, PsdLayer layer, string blend, PsdPixels? clip, PsdLayer? stopAt = null)
    {
        float opacity = OpacityOf(layer, stopAt);
        // レイヤー効果は塗りの不透明度の影響を受けない
        float fxOpacity = EffectOpacityOf(layer, stopAt);
        if (fxOpacity <= 0) return;
        var folderMasks = FolderMasks(psd, layer, stopAt);
        if (layer.Kind == PsdLayerKind.Adjustment)
        {
            if (layer.Adjustment != null && opacity > 0) Adjust(psd, acc, layer, layer.Adjustment, opacity, folderMasks, clip);
            return;
        }
        var px = layer.Kind == PsdLayerKind.Fill ? FillPixels(psd, layer) : psd.GetPixels(layer);
        if (px == null) return;
        var parts = layer.Effects is { HasRenderable: true } fx ? EffectParts(psd, layer, fx, px) : null;
        if (parts != null)
            foreach (var p in parts.Where(p => !p.Above)) DrawPixels(acc, p.Pixels, p.Blend, fxOpacity * p.Opacity, clip, folderMasks);
        if (opacity > 0) DrawPixels(acc, px, blend, opacity, clip, folderMasks);
        if (parts != null)
            foreach (var p in parts.Where(p => p.Above)) DrawPixels(acc, p.Pixels, p.Blend, fxOpacity * p.Opacity, clip, folderMasks);
    }

    /// <summary>効果の絵（PSD ごとに覚えておく）。</summary>
    private static List<PsdEffects.Part> EffectParts(PsdFile psd, PsdLayer layer, PsdLayerEffects fx, PsdPixels content)
    {
        lock (psd.EffectCache)
        {
            if (psd.EffectCache.TryGetValue(layer, out var cached)) return cached;
        }
        var parts = PsdEffects.Render(fx, content, psd.Canvas);
        lock (psd.EffectCache)
        {
            long bytes = parts.Sum(p => (long)p.Pixels.Bgra.Length);
            if (psd.EffectCacheBytes + bytes > psd.CacheLimitBytes / 2) { psd.EffectCache.Clear(); psd.EffectCacheBytes = 0; }
            psd.EffectCache[layer] = parts;
            psd.EffectCacheBytes += bytes;
        }
        return parts;
    }

    private static float EffectOpacityOf(PsdLayer l, PsdLayer? stopAt)
    {
        float o = l.Opacity / 255f;
        for (var p = l.Parent; p != null && p != stopAt; p = p.Parent) o *= p.Opacity / 255f;
        return o;
    }

    /// <summary>1 枚の絵を、描画モード・不透明度・クリッピング・フォルダのマスクを付けて重ねる。</summary>
    private static void DrawPixels(Accum acc, PsdPixels px, string blend, float opacity, PsdPixels? clip, List<PsdMask> folderMasks)
    {
        if (opacity <= 0) return;
        var rect = px.Bounds.Intersect(acc.Area);
        if (rect.IsEmpty) return;
        int mode = BlendIndex(blend);
        int s = acc.Scale;
        // 縮小時は、出力の 1 画素 = キャンバスの (s×s) の左上 1 画素で代表させる
        int dx0 = (rect.Left - acc.Area.Left + s - 1) / s;
        int dy0 = (rect.Top - acc.Area.Top + s - 1) / s;
        int dx1 = (rect.Right - acc.Area.Left + s - 1) / s;
        int dy1 = (rect.Bottom - acc.Area.Top + s - 1) / s;
        var src = px.Bgra;
        int sw = px.Width;
        for (int dy = dy0; dy < Math.Min(dy1, acc.H); dy++)
        {
            int cy = acc.Area.Top + dy * s;
            if (cy < rect.Top || cy >= rect.Bottom) continue;
            int sy = cy - px.Bounds.Top;
            for (int dx = dx0; dx < Math.Min(dx1, acc.W); dx++)
            {
                int cx = acc.Area.Left + dx * s;
                if (cx < rect.Left || cx >= rect.Right) continue;
                int si = (sy * sw + (cx - px.Bounds.Left)) * 4;
                float sa = src[si + 3] / 255f * opacity;
                if (sa <= 0) continue;
                if (clip != null) sa *= ClipAt(clip, cx, cy);
                foreach (var m in folderMasks) sa *= m.At(cx, cy);
                if (sa <= 0) continue;
                float sr = src[si + 2] / 255f, sg = src[si + 1] / 255f, sb = src[si] / 255f;
                int di = dy * acc.W + dx;
                float ab = acc.A[di];
                float cr = sr, cg = sg, cb = sb;
                if (mode != 0 && ab > 0)
                {
                    float br = acc.R[di] / ab, bg = acc.G[di] / ab, bb = acc.B[di] / ab;
                    var (mr, mg, mb) = Blend(mode, br, bg, bb, sr, sg, sb);
                    cr = (1 - ab) * sr + ab * mr;
                    cg = (1 - ab) * sg + ab * mg;
                    cb = (1 - ab) * sb + ab * mb;
                }
                acc.R[di] = sa * cr + (1 - sa) * acc.R[di];
                acc.G[di] = sa * cg + (1 - sa) * acc.G[di];
                acc.B[di] = sa * cb + (1 - sa) * acc.B[di];
                acc.A[di] = sa + ab * (1 - sa);
            }
        }
    }

    private static float ClipAt(PsdPixels clip, int cx, int cy)
    {
        int kx = cx - clip.Bounds.Left, ky = cy - clip.Bounds.Top;
        return kx >= 0 && ky >= 0 && kx < clip.Width && ky < clip.Height ? clip.Bgra[(ky * clip.Width + kx) * 4 + 3] / 255f : 0f;
    }

    /// <summary>調整レイヤー: いままで重ねた絵の色を変える（不透明度・マスク・クリッピングの分だけ）。</summary>
    private static void Adjust(PsdFile psd, Accum acc, PsdLayer layer, PsdAdjustment adj, float opacity, List<PsdMask> folderMasks, PsdPixels? clip)
    {
        var mask = psd.GetMask(layer);
        int s = acc.Scale;
        for (int dy = 0; dy < acc.H; dy++)
        {
            int cy = acc.Area.Top + dy * s;
            for (int dx = 0; dx < acc.W; dx++)
            {
                int di = dy * acc.W + dx;
                float a = acc.A[di];
                if (a <= 0) continue;
                int cx = acc.Area.Left + dx * s;
                float f = opacity;
                if (mask != null) f *= mask.At(cx, cy);
                if (clip != null) f *= ClipAt(clip, cx, cy);
                foreach (var m in folderMasks) f *= m.At(cx, cy);
                if (f <= 0) continue;
                float r = acc.R[di] / a, g = acc.G[di] / a, b = acc.B[di] / a;
                var (nr, ng, nb) = adj.Apply(r, g, b);
                acc.R[di] = (r + (nr - r) * f) * a;
                acc.G[di] = (g + (ng - g) * f) * a;
                acc.B[di] = (b + (nb - b) * f) * a;
            }
        }
    }

    // ================= 描画モード =================

    private static readonly string[] BlendOrder =
        ["norm", "mul ", "scrn", "over", "dark", "lite", "lddg", "div ", "hLit", "sLit", "idiv", "lbrn", "vLit", "lLit", "pLit", "hMix",
         "diff", "smud", "fsub", "fdiv", "dkCl", "lgCl", "hue ", "sat ", "colr", "lum "];

    /// <summary>描画モードの番号（通常・通過・ディザ合成・知らないものは 0）。</summary>
    private static int BlendIndex(string key) => Math.Max(0, Array.IndexOf(BlendOrder, key));

    /// <summary>描画モードの式（下の色 b、上の色 s、0〜1）。</summary>
    private static (float, float, float) Blend(int mode, float br, float bg, float bb, float sr, float sg, float sb)
    {
        switch (mode)
        {
            case 20: return Lum(sr, sg, sb) < Lum(br, bg, bb) ? (sr, sg, sb) : (br, bg, bb);
            case 21: return Lum(sr, sg, sb) > Lum(br, bg, bb) ? (sr, sg, sb) : (br, bg, bb);
            case 22: { var (r, g, b) = SetSat(sr, sg, sb, Sat(br, bg, bb)); return SetLum(r, g, b, Lum(br, bg, bb)); }
            case 23: { var (r, g, b) = SetSat(br, bg, bb, Sat(sr, sg, sb)); return SetLum(r, g, b, Lum(br, bg, bb)); }
            case 24: return SetLum(sr, sg, sb, Lum(br, bg, bb));
            case 25: return SetLum(br, bg, bb, Lum(sr, sg, sb));
            default: return (One(mode, br, sr), One(mode, bg, sg), One(mode, bb, sb));
        }
    }

    private static float One(int mode, float b, float s) => mode switch
    {
        1 => b * s,
        2 => b + s - b * s,
        3 => HardLight(s, b),
        4 => MathF.Min(b, s),
        5 => MathF.Max(b, s),
        6 => MathF.Min(1, b + s),
        7 => Dodge(b, s),
        8 => HardLight(b, s),
        9 => SoftLight(b, s),
        10 => Burn(b, s),
        11 => MathF.Max(0, b + s - 1),
        12 => s <= 0.5f ? Burn(b, 2 * s) : Dodge(b, 2 * s - 1),
        13 => Math.Clamp(b + 2 * s - 1, 0, 1),
        14 => s <= 0.5f ? MathF.Min(b, 2 * s) : MathF.Max(b, 2 * s - 1),
        15 => b + s >= 1 ? 1 : 0,
        16 => MathF.Abs(b - s),
        17 => b + s - 2 * b * s,
        18 => MathF.Max(0, b - s),
        19 => s <= 0 ? (b > 0 ? 1 : 0) : MathF.Min(1, b / s),
        _ => s,
    };

    private static float HardLight(float b, float s) => s <= 0.5f ? 2 * s * b : 1 - 2 * (1 - s) * (1 - b);
    private static float Dodge(float b, float s) => b <= 0 ? 0 : s >= 1 ? 1 : MathF.Min(1, b / (1 - s));
    private static float Burn(float b, float s) => b >= 1 ? 1 : s <= 0 ? 0 : 1 - MathF.Min(1, (1 - b) / s);

    private static float SoftLight(float b, float s)
    {
        if (s <= 0.5f) return b - (1 - 2 * s) * b * (1 - b);
        float d = b <= 0.25f ? ((16 * b - 12) * b + 4) * b : MathF.Sqrt(b);
        return b + (2 * s - 1) * (d - b);
    }

    // 色相・彩度・カラー・輝度の式（W3C の合成の仕様と同じ）
    private static float Lum(float r, float g, float b) => 0.3f * r + 0.59f * g + 0.11f * b;
    private static float Sat(float r, float g, float b) => MathF.Max(r, MathF.Max(g, b)) - MathF.Min(r, MathF.Min(g, b));

    private static (float, float, float) SetLum(float r, float g, float b, float l)
    {
        float d = l - Lum(r, g, b);
        r += d; g += d; b += d;
        float lum = Lum(r, g, b), n = MathF.Min(r, MathF.Min(g, b)), x = MathF.Max(r, MathF.Max(g, b));
        if (n < 0) { r = lum + (r - lum) * lum / (lum - n); g = lum + (g - lum) * lum / (lum - n); b = lum + (b - lum) * lum / (lum - n); }
        if (x > 1) { r = lum + (r - lum) * (1 - lum) / (x - lum); g = lum + (g - lum) * (1 - lum) / (x - lum); b = lum + (b - lum) * (1 - lum) / (x - lum); }
        return (r, g, b);
    }

    private static (float, float, float) SetSat(float r, float g, float b, float s)
    {
        float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
        if (max - min <= 0) return (0, 0, 0);
        float F(float c) => c == max ? s : c == min ? 0 : (c - min) * s / (max - min);
        return (F(r), F(g), F(b));
    }

    /// <summary>PSD で表示されている、重ねられるレイヤー（統合画像との見比べ用）。</summary>
    public static IEnumerable<PsdLayer> VisibleLayers(PsdFile psd) =>
        psd.ComposableLayers.Where(l => !l.EffectivelyHidden && !(l.Clipped && (ClipBaseOf(psd, l)?.EffectivelyHidden ?? false)));
}
