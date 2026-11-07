namespace BvePanelEditor.Core.Psd;

/// <summary>
/// 調整レイヤー 1 枚ぶんの色の変え方（色は 0〜1）。
/// 対応: 明るさ・コントラスト、レベル補正、トーンカーブ、色相・彩度（マスター＋色彩の統一）。
/// </summary>
public sealed class PsdAdjustment
{
    public required string Name { get; init; }
    /// <summary>Photoshop と同じにならないかもしれない点（なければ null）。</summary>
    public string? Note { get; init; }
    public required Func<float, float, float, (float R, float G, float B)> Apply { get; init; }
}

public static class PsdAdjust
{
    /// <summary>対応している調整レイヤーのキー（レイヤーの追加情報）。</summary>
    public static readonly string[] Keys = ["brit", "CgEd", "levl", "curv", "hue2"];

    /// <summary>調整レイヤーの名前（キーから）。</summary>
    public static string Label(string key) => key switch
    {
        "levl" => "レベル補正", "curv" => "トーンカーブ", "hue " or "hue2" => "色相・彩度", "brit" or "CgEd" => "明るさ・コントラスト",
        "blnc" => "カラーバランス", "expA" => "露光量", "vibA" => "自然な彩度", "mixr" => "チャンネルミキサー", "post" => "ポスタリゼーション",
        "thrs" => "2 階調化", "grdm" => "グラデーションマップ", "selc" => "特定色域の選択", "phfl" => "レンズフィルター", "clrL" => "カラールックアップ",
        "nvrt" => "階調の反転", "blwh" => "白黒", _ => key.Trim(),
    };

    /// <summary>レイヤーの追加情報から作る。対応していなければ null。</summary>
    public static PsdAdjustment? Parse(IReadOnlyDictionary<string, byte[]> blocks)
    {
        try
        {
            if (blocks.TryGetValue("levl", out var levl)) return Levels(levl);
            if (blocks.TryGetValue("curv", out var curv)) return Curves(curv);
            if (blocks.TryGetValue("hue2", out var hue)) return HueSaturation(hue);
            if (blocks.TryGetValue("CgEd", out var cged) && PsdDescriptor.ReadVersioned(cged) is { } d)
            {
                double b = PsdDescriptor.Number(d, "Brgh") ?? 0, c = PsdDescriptor.Number(d, "Cntr") ?? 0;
                bool legacy = d.TryGetValue("useLegacy", out var u) && u is true;
                return BrightnessContrast(b, c, legacy);
            }
            if (blocks.TryGetValue("brit", out var brit) && brit.Length >= 4)
                return BrightnessContrast(I16(brit, 0), I16(brit, 2), legacy: true);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException) { }
        return null;
    }

    private static int U16(byte[] d, int p) => d[p] << 8 | d[p + 1];
    private static short I16(byte[] d, int p) => (short)U16(d, p);
    private static float Clamp(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

    // ================= 明るさ・コントラスト =================

    private static PsdAdjustment BrightnessContrast(double brightness, double contrast, bool legacy)
    {
        Func<float, float> f;
        if (legacy)
        {
            // 従来方式: 明るさは足し算、コントラストは 0.5 を中心に傾ける
            float add = (float)(brightness / 255.0);
            float c = (float)Math.Clamp(contrast / 100.0, -1, 0.99);
            float slope = c >= 0 ? 1 / (1 - c) : 1 + c;
            f = v => Clamp((v + add - 0.5f) * slope + 0.5f);
        }
        else
        {
            // 今の方式（式は公開されていないので近似）: 明るさはガンマ、コントラストは 0.5 を中心にした傾き
            float gamma = (float)Math.Pow(2, -brightness / 150.0);
            float c = (float)Math.Clamp(contrast / 100.0, -1, 0.95);
            float slope = (float)Math.Tan((c + 1) * Math.PI / 4);
            f = v => Clamp((MathF.Pow(Clamp(v), gamma) - 0.5f) * slope + 0.5f);
        }
        var lut = Lut(f);
        return new PsdAdjustment
        {
            Name = "明るさ・コントラスト",
            Note = legacy ? null : "明るさ・コントラストは近似です（Photoshop と少し違うことがあります）",
            Apply = (r, g, b) => (Look(lut, r), Look(lut, g), Look(lut, b)),
        };
    }

    // ================= レベル補正 =================

    private static PsdAdjustment Levels(byte[] d)
    {
        // 版(2) の後に 29 組 ×（入力の黒・入力の白・出力の黒・出力の白・ガンマ×100）。0 = RGB、1〜3 = R・G・B
        var luts = new float[4][];
        for (int k = 0; k < 4; k++)
        {
            int p = 2 + k * 10;
            float inLo = U16(d, p) / 255f, inHi = U16(d, p + 2) / 255f, outLo = U16(d, p + 4) / 255f, outHi = U16(d, p + 6) / 255f;
            float gamma = Math.Max(1, U16(d, p + 8)) / 100f;
            luts[k] = Lut(v =>
            {
                float t = inHi > inLo ? Clamp((v - inLo) / (inHi - inLo)) : v >= inHi ? 1 : 0;
                t = MathF.Pow(t, 1 / gamma);
                return Clamp(outLo + t * (outHi - outLo));
            });
        }
        return new PsdAdjustment
        {
            Name = "レベル補正",
            // 色ごとの補正の後に、RGB 全体の補正をかける
            Apply = (r, g, b) => (Look(luts[0], Look(luts[1], r)), Look(luts[0], Look(luts[2], g)), Look(luts[0], Look(luts[3], b))),
        };
    }

    // ================= トーンカーブ =================

    private static PsdAdjustment Curves(byte[] d)
    {
        int p = 0;
        bool isMap = d[p++] != 0;
        int version = U16(d, p); p += 2;
        uint countMap = (uint)(U16(d, p) << 16 | U16(d, p + 2)); p += 4;
        var channels = new List<int>();
        if (version == 1) { for (int bit = 0; bit < 32; bit++) if ((countMap & (1u << bit)) != 0) channels.Add(bit); }
        else for (int i = 0; i < countMap; i++) channels.Add(i);

        var luts = new float[]?[4];
        foreach (var ch in channels)
        {
            float[] lut;
            if (isMap)
            {
                lut = new float[256];
                for (int i = 0; i < 256; i++) lut[i] = d[p + i] / 255f;
                p += 256;
            }
            else
            {
                int n = U16(d, p); p += 2;
                var pts = new List<(float X, float Y)>();
                for (int i = 0; i < n; i++) { pts.Add((U16(d, p + 2), U16(d, p))); p += 4; } // 出力・入力の順
                lut = Spline(pts);
            }
            if (ch < 4) luts[ch] = lut;
        }
        // 後ろに「Crv 」の追加情報があれば、そちら（チャンネル番号つき）を使う
        int crv = IndexOf(d, "Crv "u8.ToArray(), p);
        if (crv >= 0 && !isMap)
        {
            int q = crv + 4 + 2;
            int count = U16(d, q) << 16 | U16(d, q + 2); q += 4;
            var extra = new float[]?[4];
            for (int i = 0; i < count && q + 4 <= d.Length; i++)
            {
                int ch = U16(d, q), n = U16(d, q + 2); q += 4;
                var pts = new List<(float X, float Y)>();
                for (int k = 0; k < n; k++) { pts.Add((U16(d, q + 2), U16(d, q))); q += 4; }
                if (ch < 4) extra[ch] = Spline(pts);
            }
            luts = extra;
        }
        float L(int k, float v) => luts[k] is { } t ? Look(t, v) : v;
        return new PsdAdjustment
        {
            Name = "トーンカーブ",
            Apply = (r, g, b) => (L(0, L(1, r)), L(0, L(2, g)), L(0, L(3, b))),
        };
    }

    private static int IndexOf(byte[] d, byte[] pat, int from)
    {
        for (int i = from; i + pat.Length <= d.Length; i++)
            if (d.AsSpan(i, pat.Length).SequenceEqual(pat)) return i;
        return -1;
    }

    /// <summary>点（0〜255）を通る自然 3 次スプラインで、256 段の表を作る。範囲の外は端の値。</summary>
    public static float[] Spline(List<(float X, float Y)> points)
    {
        var pts = points.GroupBy(p => p.X).Select(g => g.First()).OrderBy(p => p.X).ToList();
        var lut = new float[256];
        if (pts.Count == 0) { for (int i = 0; i < 256; i++) lut[i] = i / 255f; return lut; }
        if (pts.Count == 1) { Array.Fill(lut, Clamp(pts[0].Y / 255f)); return lut; }
        int n = pts.Count;
        var x = pts.Select(p => (double)p.X).ToArray();
        var y = pts.Select(p => (double)p.Y).ToArray();
        var m = new double[n]; // 2 次微分
        if (n > 2)
        {
            var a = new double[n]; var b = new double[n]; var c = new double[n]; var r = new double[n];
            for (int i = 1; i < n - 1; i++)
            {
                a[i] = (x[i] - x[i - 1]) / 6;
                b[i] = (x[i + 1] - x[i - 1]) / 3;
                c[i] = (x[i + 1] - x[i]) / 6;
                r[i] = (y[i + 1] - y[i]) / (x[i + 1] - x[i]) - (y[i] - y[i - 1]) / (x[i] - x[i - 1]);
            }
            for (int i = 2; i < n - 1; i++) { double w = a[i] / b[i - 1]; b[i] -= w * c[i - 1]; r[i] -= w * r[i - 1]; }
            for (int i = n - 2; i >= 1; i--) m[i] = (r[i] - (i + 1 < n - 1 ? c[i] * m[i + 1] : 0)) / b[i];
        }
        for (int v = 0; v < 256; v++)
        {
            double yy;
            if (v <= x[0]) yy = y[0];
            else if (v >= x[n - 1]) yy = y[n - 1];
            else
            {
                int k = 0;
                while (k < n - 2 && v > x[k + 1]) k++;
                double h = x[k + 1] - x[k], t1 = (x[k + 1] - v) / h, t2 = (v - x[k]) / h;
                yy = t1 * y[k] + t2 * y[k + 1] + ((t1 * t1 * t1 - t1) * m[k] + (t2 * t2 * t2 - t2) * m[k + 1]) * h * h / 6;
            }
            lut[v] = Clamp((float)(yy / 255));
        }
        return lut;
    }

    // ================= 色相・彩度 =================

    private static PsdAdjustment HueSaturation(byte[] d)
    {
        bool colorize = d[2] != 0;
        int cHue = I16(d, 4), cSat = I16(d, 6), cLight = I16(d, 8);
        int mHue = I16(d, 10), mSat = I16(d, 12), mLight = I16(d, 14);
        bool ranges = false;
        for (int i = 0; i < 6; i++)
        {
            int p = 16 + i * 14 + 8;
            if (p + 6 <= d.Length && (I16(d, p) != 0 || I16(d, p + 2) != 0 || I16(d, p + 4) != 0)) ranges = true;
        }
        float hue = (colorize ? 0 : mHue) / 360f, sat = (colorize ? 0 : mSat) / 100f, light = (colorize ? cLight : mLight) / 100f;
        return new PsdAdjustment
        {
            Name = "色相・彩度",
            Note = "色相・彩度は近似です" + (ranges ? "。色ごと（レッド系など）の調整は反映しません" : ""),
            Apply = (r, g, b) =>
            {
                var (h, s, l) = ToHsl(r, g, b);
                if (colorize) { h = cHue / 360f; s = Clamp(cSat / 100f); }
                else
                {
                    h = (h + hue) % 1; if (h < 0) h += 1;
                    s = sat < 0 ? s * (1 + sat) : s + (1 - s) * sat;
                }
                var (nr, ng, nb) = FromHsl(h, Clamp(s), l);
                if (light > 0) { nr += (1 - nr) * light; ng += (1 - ng) * light; nb += (1 - nb) * light; }
                else if (light < 0) { nr *= 1 + light; ng *= 1 + light; nb *= 1 + light; }
                return (Clamp(nr), Clamp(ng), Clamp(nb));
            },
        };
    }

    public static (float H, float S, float L) ToHsl(float r, float g, float b)
    {
        float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
        float l = (max + min) / 2;
        if (max - min < 1e-6f) return (0, 0, l);
        float d = max - min;
        float s = l > 0.5f ? d / (2 - max - min) : d / (max + min);
        float h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h / 6, s, l);
    }

    public static (float R, float G, float B) FromHsl(float h, float s, float l)
    {
        if (s <= 0) return (l, l, l);
        float q = l < 0.5f ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        static float Hue(float p, float q, float t)
        {
            if (t < 0) t += 1; if (t > 1) t -= 1;
            return t < 1f / 6 ? p + (q - p) * 6 * t : t < 0.5f ? q : t < 2f / 3 ? p + (q - p) * (2f / 3 - t) * 6 : p;
        }
        return (Hue(p, q, h + 1f / 3), Hue(p, q, h), Hue(p, q, h - 1f / 3));
    }

    // ================= 表 =================

    private static float[] Lut(Func<float, float> f)
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++) t[i] = f(i / 255f);
        return t;
    }

    /// <summary>表を線形補間で引く。</summary>
    private static float Look(float[] lut, float v)
    {
        float x = Clamp(v) * 255;
        int i = (int)x;
        if (i >= 255) return lut[255];
        float f = x - i;
        return lut[i] + (lut[i + 1] - lut[i]) * f;
    }
}
