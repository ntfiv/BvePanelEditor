using BvePanelEditor.Core.Psd;

namespace BvePanelEditor.Core;

/// <summary>パネル本体の画像で、絵がきちんと描かれている範囲を見つけた結果。</summary>
public sealed class PanelBoundsResult
{
    /// <summary>描かれている範囲（画像の座標）。何も描かれていなければ空。</summary>
    public PsdRect Content;
    /// <summary>余白とみなした一定の色（まわりの縁から見つけたもの）。</summary>
    public List<(byte R, byte G, byte B)> Background = [];
    /// <summary>縁に透明な所があったか。</summary>
    public bool TransparentEdge;
}

/// <summary>パネル本体の表示範囲（[This] の Left・Top・Right・Bottom）を、画像の描かれている範囲から計算する。</summary>
public static class PanelBounds
{
    /// <summary>
    /// 描かれている範囲を見つける。透明な所と、画像の縁を埋めている一定の色（許容差 tolerance 以内）を「余白」とみなし、
    /// 上下左右の端から内側へ見ていって、余白でない画素が行（列）の minFraction 以上ある所までを外側とする。
    /// </summary>
    /// <param name="useFillColor">false なら透明な所だけを余白とみなす。</param>
    public static PanelBoundsResult Detect(PsdPixels img, int tolerance = 12, double minFraction = 0.01, bool useFillColor = true)
    {
        var r = new PanelBoundsResult();
        int w = img.Width, h = img.Height;
        if (w == 0 || h == 0) return r;
        var px = img.Bgra;

        // 縁の画素から、余白の色を探す（縁の 20% 以上を占める色。3 色まで）
        var buckets = new Dictionary<int, (long R, long G, long B, int N)>();
        int edgeCount = 0, transparent = 0;
        void Edge(int x, int y)
        {
            int i = (y * w + x) * 4;
            edgeCount++;
            if (px[i + 3] < 16) { transparent++; return; }
            int key = (px[i + 2] >> 3) << 10 | (px[i + 1] >> 3) << 5 | px[i] >> 3;
            var b = buckets.GetValueOrDefault(key);
            buckets[key] = (b.R + px[i + 2], b.G + px[i + 1], b.B + px[i], b.N + 1);
        }
        for (int x = 0; x < w; x++) { Edge(x, 0); if (h > 1) Edge(x, h - 1); }
        for (int y = 1; y < h - 1; y++) { Edge(0, y); if (w > 1) Edge(w - 1, y); }
        r.TransparentEdge = transparent > 0;
        if (useFillColor)
            foreach (var b in buckets.Values.Where(b => b.N >= edgeCount * 0.2).OrderByDescending(b => b.N).Take(3))
                r.Background.Add(((byte)(b.R / b.N), (byte)(b.G / b.N), (byte)(b.B / b.N)));

        bool Empty(int i)
        {
            if (px[i + 3] < 16) return true;
            foreach (var (cr, cg, cb) in r.Background)
                if (Math.Abs(px[i + 2] - cr) <= tolerance && Math.Abs(px[i + 1] - cg) <= tolerance && Math.Abs(px[i] - cb) <= tolerance) return true;
            return false;
        }

        // 行・列ごとに、余白でない画素を数える
        var rows = new int[h];
        var cols = new int[w];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (!Empty((y * w + x) * 4)) { rows[y]++; cols[x]++; }

        int rowMin = Math.Max(3, (int)Math.Ceiling(w * minFraction)), colMin = Math.Max(3, (int)Math.Ceiling(h * minFraction)); // ぽつんとある汚れは数えない
        int top = Array.FindIndex(rows, c => c >= rowMin);
        if (top < 0) return r;
        int bottom = Array.FindLastIndex(rows, c => c >= rowMin);
        int left = Array.FindIndex(cols, c => c >= colMin);
        int right = Array.FindLastIndex(cols, c => c >= colMin);
        if (left < 0) return r;
        r.Content = new PsdRect(img.Bounds.Left + left, img.Bounds.Top + top, img.Bounds.Left + right + 1, img.Bounds.Top + bottom + 1);
        return r;
    }

    /// <summary>2 枚（昼と夜など）の範囲の重なり。片方が空ならもう片方。</summary>
    public static PsdRect Overlap(PsdRect a, PsdRect b) => a.IsEmpty ? b : b.IsEmpty ? a : a.Intersect(b);

    /// <summary>範囲を内側へ margin だけ縮める（縮めすぎて裏返るなら空）。</summary>
    public static PsdRect Shrink(PsdRect r, int margin)
    {
        var s = new PsdRect(r.Left + margin, r.Top + margin, r.Right - margin, r.Bottom - margin);
        return s.Right <= s.Left || s.Bottom <= s.Top ? default : s;
    }
}
