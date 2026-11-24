namespace BvePanelEditor.Core.Imaging;

/// <summary>
/// 射影変換（3×3 の行列、最後の要素は 1）。四角い絵を 4 隅を決めた形に写すのに使う（モニタくんの 4 隅の変形と同じ計算）。
/// </summary>
public sealed class Homography
{
    private readonly double[] _m;

    private Homography(double[] m) { _m = m; }

    /// <summary>四角 (0,0)-(w,h) の 4 隅を、指定の 4 点（左上・右上・右下・左下）に写す。</summary>
    public static Homography FromRectToQuad(double w, double h, (double X, double Y) tl, (double X, double Y) tr, (double X, double Y) br, (double X, double Y) bl) =>
        FromPoints([(0, 0), (w, 0), (w, h), (0, h)], [tl, tr, br, bl]);

    /// <summary>4 組の点の対応から求める。4 点が一直線に並ぶなど決められなければ InvalidOperationException。</summary>
    public static Homography FromPoints(IReadOnlyList<(double X, double Y)> src, IReadOnlyList<(double X, double Y)> dst)
    {
        // 8 元連立方程式 A·h = b を、部分ピボット付きの掃き出しで解く
        var a = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            double x = src[i].X, y = src[i].Y, u = dst[i].X, v = dst[i].Y;
            int r0 = i * 2, r1 = r0 + 1;
            a[r0, 0] = x; a[r0, 1] = y; a[r0, 2] = 1; a[r0, 6] = -x * u; a[r0, 7] = -y * u; a[r0, 8] = u;
            a[r1, 3] = x; a[r1, 4] = y; a[r1, 5] = 1; a[r1, 6] = -x * v; a[r1, 7] = -y * v; a[r1, 8] = v;
        }
        for (int col = 0; col < 8; col++)
        {
            int piv = col;
            for (int r = col + 1; r < 8; r++) if (Math.Abs(a[r, col]) > Math.Abs(a[piv, col])) piv = r;
            if (Math.Abs(a[piv, col]) < 1e-12) throw new InvalidOperationException("4 隅が一直線に並んでいて変形を決められません。");
            if (piv != col) for (int c = 0; c < 9; c++) (a[col, c], a[piv, c]) = (a[piv, c], a[col, c]);
            for (int r = 0; r < 8; r++)
            {
                if (r == col) continue;
                double f = a[r, col] / a[col, col];
                if (f == 0) continue;
                for (int c = col; c < 9; c++) a[r, c] -= f * a[col, c];
            }
        }
        var m = new double[9];
        for (int i = 0; i < 8; i++) m[i] = a[i, 8] / a[i, i];
        m[8] = 1;
        return new Homography(m);
    }

    public (double X, double Y) Map(double x, double y)
    {
        double w = _m[6] * x + _m[7] * y + _m[8];
        return ((_m[0] * x + _m[1] * y + _m[2]) / w, (_m[3] * x + _m[4] * y + _m[5]) / w);
    }

    public Homography Inverse()
    {
        var m = _m;
        double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
        double A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
        double det = a * A + b * B + c * C;
        if (Math.Abs(det) < 1e-15) throw new InvalidOperationException("逆向きの変換を求められません。");
        return new Homography(
        [
            A / det, -(b * i - c * h) / det, (b * f - c * e) / det,
            B / det, (a * i - c * g) / det, -(a * f - c * d) / det,
            C / det, -(a * h - b * g) / det, (a * e - b * d) / det,
        ]);
    }

    /// <summary>四角 (x, y, w, h) を写した先を囲む四角（左・上・右・下）。</summary>
    public (double Left, double Top, double Right, double Bottom) MapBounds(double x, double y, double w, double h)
    {
        var p = new[] { Map(x, y), Map(x + w, y), Map(x + w, y + h), Map(x, y + h) };
        return (p.Min(q => q.X), p.Min(q => q.Y), p.Max(q => q.X), p.Max(q => q.Y));
    }

    /// <summary>この変換が、元の何画素を 1 画素に縮めるか（おおよそ。4 隅の辺の長さの比）。</summary>
    public double ShrinkFactor(double w, double h)
    {
        var p = new[] { Map(0, 0), Map(w, 0), Map(w, h), Map(0, h) };
        static double Len((double X, double Y) u, (double X, double Y) v) => Math.Sqrt((u.X - v.X) * (u.X - v.X) + (u.Y - v.Y) * (u.Y - v.Y));
        double sx = Math.Min(Len(p[0], p[1]), Len(p[3], p[2])), sy = Math.Min(Len(p[0], p[3]), Len(p[1], p[2]));
        if (sx < 1e-9 || sy < 1e-9) return 1;
        return Math.Max(w / sx, h / sy);
    }
}
