using BvePanelEditor.Core.Psd;

namespace BvePanelEditor.Core.Imaging;

/// <summary>
/// 画像を 4 隅で変形する（モニタくんの 4 隅の変形と同じ方法）。正面から描いたモニタやスタフの絵を、パネルの斜めの面に合わせて貼りこむのに使う。
/// 出来上がりの 1 画素の中を何点か取って平均する（縮むほど多く取る）。
/// </summary>
public static class Warp
{
    /// <summary>4 隅で囲む範囲の 1 辺の上限（画素）。</summary>
    public const int MaxSide = 8192;

    /// <summary>1 画素の中で取る点の数（縦・横それぞれ）の上限。</summary>
    public const int MaxSamples = 8;

    /// <summary>
    /// 4 隅（左上・右上・右下・左下の順）で変形できるか確かめる。できなければ InvalidOperationException。
    /// 辺が交差した形・へこんだ形・一直線に近い形は、射影変換で絵が裏返ったり、限りなく伸びたりするので断る。
    /// 時計回り・反時計回り（裏返し）のどちらの並びでもよい。囲む範囲が 1 辺 MaxSide を超えるものも断る。
    /// </summary>
    public static void CheckQuad((double X, double Y) tl, (double X, double Y) tr, (double X, double Y) br, (double X, double Y) bl)
    {
        (double X, double Y)[] p = [tl, tr, br, bl];
        if (p.Any(q => !double.IsFinite(q.X) || !double.IsFinite(q.Y))) throw new InvalidOperationException("4 隅の座標が数になっていません。");
        double l = p.Min(q => q.X), t = p.Min(q => q.Y), r = p.Max(q => q.X), b = p.Max(q => q.Y);
        if (r - l > MaxSide || b - t > MaxSide) throw new InvalidOperationException($"4 隅で囲む範囲が大きすぎます（1 辺 {MaxSide} 画素まで）。");
        // 隣り合う辺の向きの変わり方（外積）が、4 つの角でみな同じ向きなら、へこみも交差もない
        int sign = 0;
        for (int i = 0; i < 4; i++)
        {
            var a = p[i]; var c = p[(i + 1) % 4]; var d = p[(i + 2) % 4];
            double z = (c.X - a.X) * (d.Y - c.Y) - (c.Y - a.Y) * (d.X - c.X);
            int s = Math.Abs(z) < 1e-9 ? 0 : Math.Sign(z);
            if (s == 0 || (sign != 0 && s != sign))
                throw new InvalidOperationException("4 隅を結んだ辺が交差しているか、へこんだ形か、一直線に並んでいて変形できません。");
            sign = s;
        }
    }

    /// <summary>
    /// 絵 src（左上を (0, 0) として）を、4 隅（左上・右上・右下・左下、パネルの座標）に写す。
    /// 返す絵の Bounds は、写した先を囲む整数の四角（パネルの座標）。maxSamples は 1 画素の中で取る点の数の上限（縦・横それぞれ。下書きは 1）。
    /// </summary>
    public static PsdPixels ToQuad(PsdPixels src, (double X, double Y) tl, (double X, double Y) tr, (double X, double Y) br, (double X, double Y) bl, int maxSamples = MaxSamples)
    {
        CheckQuad(tl, tr, br, bl);
        return Apply(src, Homography.FromRectToQuad(src.Width, src.Height, tl, tr, br, bl), maxSamples);
    }

    /// <summary>
    /// DigitalNumber のコマ割済の絵（1 コマの高さ interval で縦に並ぶ）を、1 コマずつ同じ 4 隅で写して縦に並べ直す。
    /// 返すのは並べ直した絵（Bounds の左上が置く位置）と、変形後の 1 コマの高さ。絵の高さが interval で割り切れなければ、余りの所は使わない。
    /// </summary>
    public static (PsdPixels Strip, int Interval) FramesToQuad(PsdPixels src, int interval, (double X, double Y) tl, (double X, double Y) tr, (double X, double Y) br, (double X, double Y) bl)
    {
        if (interval <= 0 || interval > src.Height) throw new ArgumentOutOfRangeException(nameof(interval));
        CheckQuad(tl, tr, br, bl);
        int frames = src.Height / interval;
        var h = Homography.FromRectToQuad(src.Width, interval, tl, tr, br, bl);
        PsdPixels? first = null;
        byte[]? strip = null;
        for (int f = 0; f < frames; f++)
        {
            var w = Apply(Crop(src, 0, f * interval, src.Width, interval), h);
            if (first == null)
            {
                first = w;
                strip = new byte[w.Bgra.Length * frames];
            }
            Buffer.BlockCopy(w.Bgra, 0, strip!, f * w.Bgra.Length, w.Bgra.Length);
        }
        var b = first!.Bounds;
        return (new PsdPixels(new PsdRect(b.Left, b.Top, b.Right, b.Top + b.Height * frames), strip!), b.Height);
    }

    /// <summary>絵 src（左上を (0, 0) として）を h で写す。maxSamples は 1 画素の中で取る点の数の上限（縦・横それぞれ）。</summary>
    public static PsdPixels Apply(PsdPixels src, Homography h, int maxSamples = MaxSamples)
    {
        var (l, t, r, btm) = h.MapBounds(0, 0, src.Width, src.Height);
        int x0 = (int)Math.Floor(l + 1e-6), y0 = (int)Math.Floor(t + 1e-6);
        int x1 = (int)Math.Ceiling(r - 1e-6), y1 = (int)Math.Ceiling(btm - 1e-6);
        int w = Math.Max(0, x1 - x0), hgt = Math.Max(0, y1 - y0);
        var dst = new byte[w * hgt * 4];
        if (w == 0 || hgt == 0 || src.Width == 0 || src.Height == 0) return new PsdPixels(new PsdRect(x0, y0, x0 + w, y0 + hgt), dst);

        var inv = h.Inverse();
        int s = Math.Clamp((int)Math.Ceiling(h.ShrinkFactor(src.Width, src.Height) - 1e-6), 1, Math.Clamp(maxSamples, 1, MaxSamples));
        float invCount = 1f / (s * s);
        var p = src.Bgra;
        int sw = src.Width, sh = src.Height;

        Parallel.For(0, hgt, yy =>
        {
            for (int xx = 0; xx < w; xx++)
            {
                float sb = 0, sg = 0, sr = 0, sa = 0;
                for (int j = 0; j < s; j++)
                {
                    double py = y0 + yy + (j + 0.5) / s;
                    for (int i = 0; i < s; i++)
                    {
                        double px = x0 + xx + (i + 0.5) / s;
                        var q = inv.Map(px, py);
                        // 元の画素の中心は (n + 0.5)
                        Bilinear(p, sw, sh, q.X - 0.5, q.Y - 0.5, ref sb, ref sg, ref sr, ref sa);
                    }
                }
                if (sa <= 0) continue;
                int o = (yy * w + xx) * 4;
                // 色は不透明度を掛けて混ぜたので、不透明度で割って戻す（透明な所の色が縁ににじまないように）
                dst[o] = ToByte(sb / sa * 255);
                dst[o + 1] = ToByte(sg / sa * 255);
                dst[o + 2] = ToByte(sr / sa * 255);
                dst[o + 3] = ToByte(sa * invCount);
            }
        });
        return new PsdPixels(new PsdRect(x0, y0, x0 + w, y0 + hgt), dst);
    }

    /// <summary>絵の一部（左上を (0, 0) とした座標）を切り出す。返す絵の Bounds は (0, 0) 起点。はみ出した所は透明。</summary>
    public static PsdPixels Crop(PsdPixels src, int x, int y, int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int row = 0; row < h; row++)
        {
            int sy = y + row;
            if (sy < 0 || sy >= src.Height) continue;
            int sx0 = Math.Max(0, x), sx1 = Math.Min(src.Width, x + w);
            if (sx1 <= sx0) continue;
            Buffer.BlockCopy(src.Bgra, (sy * src.Width + sx0) * 4, px, (row * w + (sx0 - x)) * 4, (sx1 - sx0) * 4);
        }
        return new PsdPixels(new PsdRect(0, 0, w, h), px);
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)(v + 0.5f), 0, 255);

    private static void Bilinear(byte[] p, int w, int h, double x, double y, ref float b, ref float g, ref float r, ref float a)
    {
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        float fx = (float)(x - ix), fy = (float)(y - iy);
        Add(p, w, h, ix, iy, (1 - fx) * (1 - fy), ref b, ref g, ref r, ref a);
        Add(p, w, h, ix + 1, iy, fx * (1 - fy), ref b, ref g, ref r, ref a);
        Add(p, w, h, ix, iy + 1, (1 - fx) * fy, ref b, ref g, ref r, ref a);
        Add(p, w, h, ix + 1, iy + 1, fx * fy, ref b, ref g, ref r, ref a);
    }

    private static void Add(byte[] p, int w, int h, int x, int y, float k, ref float b, ref float g, ref float r, ref float a)
    {
        if (k <= 0 || x < 0 || y < 0 || x >= w || y >= h) return;
        int o = (y * w + x) * 4;
        float al = p[o + 3] * k;
        b += p[o] * al / 255f; g += p[o + 1] * al / 255f; r += p[o + 2] * al / 255f; a += al;
    }
}
