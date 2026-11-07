using BvePanelEditor.Core.Psd;

namespace BvePanelEditor.Core;

/// <summary>見つけた蓋の丸（パネルの画像の座標）。Score は縁の色の変わり方（0〜255）。</summary>
public sealed record CoverCircleResult(double Cx, double Cy, double R, double Score);

/// <summary>
/// パネルの画像に描かれた、針の軸の蓋（丸）を見つける。
/// 針の回転の中心のまわりで、中心を少しずつずらしながら半径ごとに円周の内と外の色の差を調べ、
/// 円周の「ほぼ全部」で色が変わる（差の下から 1/4 の値が大きい）円を蓋の縁の候補とする。
/// </summary>
public static class CoverCircle
{
    /// <param name="cx">探す中心（針の回転の中心）。</param>
    /// <param name="maxR">探す半径の上限。</param>
    /// <param name="shift">中心をずらして探す幅（±px）。</param>
    public static CoverCircleResult? Detect(PsdPixels img, double cx, double cy, double maxR, int shift = 6, double minR = 4) =>
        DetectAll(img, cx, cy, maxR, shift, minR).FirstOrDefault();

    /// <summary>蓋の縁の候補を、色の変わり方の強い順に返す（文字盤の輪なども候補になるので、画面で選び直せるようにする）。</summary>
    public static List<CoverCircleResult> DetectAll(PsdPixels img, double cx, double cy, double maxR, int shift = 6, double minR = 4, double minScore = 24)
    {
        if (img.Width == 0 || maxR < minR) return [];
        const int angles = 72;
        var cos = new double[angles];
        var sin = new double[angles];
        for (int a = 0; a < angles; a++) { cos[a] = Math.Cos(a * 2 * Math.PI / angles); sin[a] = Math.Sin(a * 2 * Math.PI / angles); }
        var diffs = new double[angles];

        double Score(double x0, double y0, double r)
        {
            for (int a = 0; a < angles; a++)
            {
                var inner = Sample(img, x0 + cos[a] * (r - 1.5), y0 + sin[a] * (r - 1.5));
                var outer = Sample(img, x0 + cos[a] * (r + 1.5), y0 + sin[a] * (r + 1.5));
                diffs[a] = Math.Max(Math.Max(Math.Abs(inner.B - outer.B), Math.Abs(inner.G - outer.G)),
                    Math.Max(Math.Abs(inner.R - outer.R), Math.Abs(inner.A - outer.A)));
            }
            Array.Sort(diffs);
            return diffs[angles / 4];
        }

        // 半径ごとに、中心を 2px おきにずらしていちばん良いものを取る
        int nr = (int)Math.Floor(maxR - minR) + 1;
        var perR = new CoverCircleResult[nr];
        for (int k = 0; k < nr; k++)
        {
            double r = minR + k;
            for (int dy = -shift; dy <= shift; dy += 2)
                for (int dx = -shift; dx <= shift; dx += 2)
                {
                    var s = Score(cx + dx, cy + dy, r);
                    if (perR[k] == null || s > perR[k].Score) perR[k] = new(cx + dx, cy + dy, r, s);
                }
        }
        // 半径の向きで山になっている所を候補にし、そのまわりを 0.5px おきに細かく探す
        var found = new List<CoverCircleResult>();
        for (int k = 0; k < nr; k++)
        {
            var c = perR[k];
            if (c.Score < minScore) continue;
            if (k > 0 && perR[k - 1].Score > c.Score) continue;
            if (k < nr - 1 && perR[k + 1].Score >= c.Score) continue;
            var best = c;
            for (double dy = -1.5; dy <= 1.5; dy += 0.5)
                for (double dx = -1.5; dx <= 1.5; dx += 0.5)
                    for (double r = Math.Max(minR, c.R - 1.5); r <= Math.Min(maxR, c.R + 1.5); r += 0.5)
                    {
                        var s = Score(c.Cx + dx, c.Cy + dy, r);
                        if (s > best.Score) best = new(c.Cx + dx, c.Cy + dy, r, s);
                    }
            if (found.All(f => Math.Abs(f.R - best.R) > 2 || Math.Abs(f.Cx - best.Cx) > 2 || Math.Abs(f.Cy - best.Cy) > 2)) found.Add(best);
        }
        return found.OrderByDescending(f => f.Score).ToList();
    }

    /// <summary>画像の点の色（双線形。透明は黒として混ぜ、A は別に返す）。画像の外は透明。</summary>
    private static (double B, double G, double R, double A) Sample(PsdPixels img, double x, double y)
    {
        x -= img.Bounds.Left + 0.5;
        y -= img.Bounds.Top + 0.5;
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        double b = 0, g = 0, r = 0, a = 0;
        for (int k = 0; k < 4; k++)
        {
            int xx = x0 + (k & 1), yy = y0 + (k >> 1);
            double wgt = ((k & 1) == 1 ? fx : 1 - fx) * ((k >> 1) == 1 ? fy : 1 - fy);
            if (xx < 0 || yy < 0 || xx >= img.Width || yy >= img.Height || wgt == 0) continue;
            int i = (yy * img.Width + xx) * 4;
            double al = img.Bgra[i + 3];
            b += wgt * img.Bgra[i] * al / 255;
            g += wgt * img.Bgra[i + 1] * al / 255;
            r += wgt * img.Bgra[i + 2] * al / 255;
            a += wgt * al;
        }
        return (b, g, r, a);
    }
}
