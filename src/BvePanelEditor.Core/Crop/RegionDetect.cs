using BvePanelEditor.Core.Psd;

namespace BvePanelEditor.Core.Crop;

/// <summary>無発光と発光の画像の差から、光る所（表示灯など）の四角を見つける。</summary>
public static class RegionDetect
{
    /// <summary>色の差（RGB とアルファの最大差）が threshold を超える画素。area の中だけ（area の幅×高さの並び）。</summary>
    public static bool[] DiffMask(PsdPixels unlit, PsdPixels lit, PsdRect area, int threshold)
    {
        int w = area.Width, h = area.Height;
        var mask = new bool[w * h];
        int W = Math.Min(unlit.Width, lit.Width);
        for (int y = 0; y < h; y++)
        {
            int cy = area.Top + y;
            if (cy < 0 || cy >= Math.Min(unlit.Height, lit.Height)) continue;
            for (int x = 0; x < w; x++)
            {
                int cx = area.Left + x;
                if (cx < 0 || cx >= W) continue;
                int i = (cy * unlit.Width + cx) * 4, j = (cy * lit.Width + cx) * 4;
                int d = 0;
                for (int c = 0; c < 4; c++) d = Math.Max(d, Math.Abs(unlit.Bgra[i + c] - lit.Bgra[j + c]));
                mask[y * w + x] = d > threshold;
            }
        }
        return mask;
    }

    /// <summary>
    /// 光る所の四角を見つける。差のある画素を gap だけ太らせてつなげ、つながりごとに（太らせる前の画素の）外枠を取る。
    /// 差のある画素が minPixels より少ないものは捨てる。外枠は pad だけ広げ、area（既定はキャンバス全体）の中に収める。
    /// 上から、同じ高さなら左から順に並べる。
    /// </summary>
    public static List<PsdRect> Detect(PsdPixels unlit, PsdPixels lit, int threshold = 24, int gap = 6, int minPixels = 20, PsdRect? area = null, int pad = 2)
    {
        var a = area ?? new PsdRect(0, 0, Math.Min(unlit.Width, lit.Width), Math.Min(unlit.Height, lit.Height));
        int w = a.Width, h = a.Height;
        if (w <= 0 || h <= 0) return [];
        var mask = DiffMask(unlit, lit, a, threshold);
        var grown = gap > 0 ? Dilate(mask, w, h, gap) : mask;

        var label = new int[w * h];
        var stack = new Stack<int>();
        var result = new List<PsdRect>();
        int next = 0;
        for (int start = 0; start < w * h; start++)
        {
            if (!grown[start] || label[start] != 0) continue;
            next++;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1, count = 0;
            label[start] = next;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int px = p % w, py = p / w;
                if (mask[p])
                {
                    count++;
                    if (px < x0) x0 = px;
                    if (px > x1) x1 = px;
                    if (py < y0) y0 = py;
                    if (py > y1) y1 = py;
                }
                if (px > 0) Visit(p - 1);
                if (px < w - 1) Visit(p + 1);
                if (py > 0) Visit(p - w);
                if (py < h - 1) Visit(p + w);
            }
            if (count < minPixels || x1 < 0) continue;
            var r = new PsdRect(a.Left + x0 - pad, a.Top + y0 - pad, a.Left + x1 + 1 + pad, a.Top + y1 + 1 + pad).Intersect(a);
            if (!r.IsEmpty) result.Add(r);

            void Visit(int q)
            {
                if (!grown[q] || label[q] != 0) return;
                label[q] = next;
                stack.Push(q);
            }
        }
        return result.OrderBy(r => r.Top / 8).ThenBy(r => r.Left).ToList();
    }

    /// <summary>色の差が threshold を超える画素の数（重なる範囲だけ）。</summary>
    public static long CountDiff(PsdPixels a, PsdPixels b, int threshold)
    {
        int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height);
        long n = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * a.Width + x) * 4, j = (y * b.Width + x) * 4;
                int d = 0;
                for (int c = 0; c < 4; c++) d = Math.Max(d, Math.Abs(a.Bgra[i + c] - b.Bgra[j + c]));
                if (d > threshold) n++;
            }
        return n;
    }

    /// <summary>
    /// 単純な変化の差で探すときの比べる相手: 部品用の画像（part）に近い方（差のある画素が少ない方）。
    /// 発光の画像から作った部品用の画像なら発光、無発光から作ったものなら無発光になり、表示灯の差を拾わない。同じなら無発光。
    /// </summary>
    public static bool NearerIsLit(PsdPixels part, PsdPixels unlit, PsdPixels? lit, int threshold) =>
        lit != null && CountDiff(part, lit, threshold) < CountDiff(part, unlit, threshold);

    /// <summary>1 つの囲みの中を、つなげずに検出し直して分ける（横並びの小さな灯など）。分けられなければ元のまま。</summary>
    /// <summary>
    /// 感度を画像に合わせて決めて探す。
    /// 1. threshold で探して、絵全体の 4 分の 1 以上を覆う囲みがあり、囲みが 2 個以下（全体が 1 つにまとまった）なら、
    ///    感度を 1.5 倍ずつ上げて探し直す（4 点の色が全体に少しずつ違う画像）
    /// 2. それでも残った、絵全体の 4 分の 1 以上を覆う囲みは部品ではないので外す（発光の絵が広く変わっている所など）
    /// 使った感度と、外した囲みの数も返す。
    /// </summary>
    public static (List<PsdRect> Rects, int Threshold, int RemovedLarge) DetectAuto(PsdPixels unlit, PsdPixels lit, int threshold, int gap, int maxThreshold = 100)
    {
        long canvas = (long)Math.Min(unlit.Width, lit.Width) * Math.Min(unlit.Height, lit.Height);
        bool Big(PsdRect r) => (long)r.Width * r.Height * 4 >= canvas;
        int t = Math.Max(1, threshold);
        var rects = Detect(unlit, lit, t, gap);
        while (rects.Count <= 2 && rects.Any(Big) && t < maxThreshold)
        {
            t = Math.Min(maxThreshold, Math.Max(t + 1, (int)Math.Round(t * 1.5)));
            rects = Detect(unlit, lit, t, gap);
        }
        int removed = rects.RemoveAll(Big);
        return (rects, t, removed);
    }

    public static List<PsdRect> Split(PsdPixels unlit, PsdPixels lit, PsdRect rect, int threshold = 24)
    {
        var parts = Detect(unlit, lit, threshold, gap: 1, minPixels: 6, area: rect, pad: 1);
        return parts.Count >= 2 ? parts : [rect];
    }

    /// <summary>四角をまとめた外枠。</summary>
    public static PsdRect Union(IEnumerable<PsdRect> rects) => rects.Aggregate(default(PsdRect), (u, r) => u.Union(r));

    /// <summary>正方形の範囲で太らせる（横、縦の順に、範囲内に 1 つでもあれば true）。</summary>
    private static bool[] Dilate(bool[] m, int w, int h, int r)
    {
        var tmp = new bool[w * h];
        var prefix = new int[Math.Max(w, h) + 1];
        for (int y = 0; y < h; y++)
        {
            prefix[0] = 0;
            for (int x = 0; x < w; x++) prefix[x + 1] = prefix[x] + (m[y * w + x] ? 1 : 0);
            for (int x = 0; x < w; x++)
            {
                int lo = Math.Max(0, x - r), hi = Math.Min(w, x + r + 1);
                tmp[y * w + x] = prefix[hi] - prefix[lo] > 0;
            }
        }
        var res = new bool[w * h];
        for (int x = 0; x < w; x++)
        {
            prefix[0] = 0;
            for (int y = 0; y < h; y++) prefix[y + 1] = prefix[y] + (tmp[y * w + x] ? 1 : 0);
            for (int y = 0; y < h; y++)
            {
                int lo = Math.Max(0, y - r), hi = Math.Min(h, y + r + 1);
                res[y * w + x] = prefix[hi] - prefix[lo] > 0;
            }
        }
        return res;
    }

    /// <summary>画像の一部を切り出す（はみ出した所は透明）。</summary>
    public static PsdPixels CropOf(PsdPixels img, PsdRect r)
    {
        var bgra = new byte[r.Width * r.Height * 4];
        for (int y = 0; y < r.Height; y++)
        {
            int sy = r.Top + y - img.Bounds.Top;
            if (sy < 0 || sy >= img.Height) continue;
            int x0 = Math.Max(r.Left, img.Bounds.Left), x1 = Math.Min(r.Right, img.Bounds.Right);
            if (x1 <= x0) continue;
            Buffer.BlockCopy(img.Bgra, (sy * img.Width + (x0 - img.Bounds.Left)) * 4, bgra, (y * r.Width + (x0 - r.Left)) * 4, (x1 - x0) * 4);
        }
        return new PsdPixels(r, bgra);
    }
}
