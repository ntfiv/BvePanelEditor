using BvePanelEditor.Core.Psd;

namespace BvePanelEditor.Core;

/// <summary>デジタル数字（DigitalNumber）の画像から見つけた 1 コマの高さ。Confident が false なら推定。</summary>
public sealed record DigitalFramesResult(int Interval, int Frames, string Reason, bool Confident);

/// <summary>
/// デジタル数字の、縦に並んだコマの画像から 1 コマの高さ（Interval）を見つける。
/// 1. 字のある行のかたまりが、どれも 1 コマに 1 つずつ同じ位置に入る高さ（いちばん大きいもの）
/// 2. 1 で決まらなければ、行ごとの字の量のくり返し（周期）。描かれている所がちょうどコマの数で終わる高さを優先する
/// 3. それでも決まらなければ、よくあるコマ数で割り切れる高さ
/// </summary>
public static class DigitalFrames
{
    /// <summary>調べる用: 最後に見つけた字のある行のかたまりと、余白の色。</summary>
    [ThreadStatic] public static List<(int Top, int Bottom)>? LastBands;
    [ThreadStatic] public static List<(byte R, byte G, byte B)>? LastBackground;

    /// <param name="height">中身の高さ（2 の累乗に広げた PNG なら広げる前の高さ）。null なら画像の高さ。</param>
    public static DigitalFramesResult? Detect(PsdPixels img, int? height = null)
    {
        int w = img.Width, h = Math.Min(height ?? img.Height, img.Height);
        if (w == 0 || h < 2) return null;
        var px = img.Bgra;

        // 描かれている所の終わり（下の透明な余りは数えない）
        int end = 0;
        for (int y = h - 1; y >= 0 && end == 0; y--)
            for (int x = 0; x < w; x++)
                if (px[(y * w + x) * 4 + 3] >= 32) { end = y + 1; break; }
        if (end == 0) return null;

        // 余白の色: 透明のほか、縁の半分以上を占める色と、描かれている所の 35% 以上を占める色（コマごとの背景）
        var bg = new List<(byte R, byte G, byte B)>();
        {
            var edge = new Dictionary<int, int>();
            int edgeCount = 0;
            void Edge(int x, int y)
            {
                int i = (y * w + x) * 4;
                edgeCount++;
                if (px[i + 3] < 32) return;
                int key = px[i + 2] << 16 | px[i + 1] << 8 | px[i];
                edge[key] = edge.GetValueOrDefault(key) + 1;
            }
            for (int x = 0; x < w; x++) { Edge(x, 0); Edge(x, end - 1); }
            for (int y = 1; y < end - 1; y++) { Edge(0, y); Edge(w - 1, y); }
            if (edge.Count > 0 && edge.MaxBy(kv => kv.Value) is var e && e.Value * 2 >= edgeCount) bg.Add(Rgb(e.Key));

            var all = new Dictionary<int, int>();
            int opaque = 0;
            for (int i = 0; i < end * w * 4; i += 4)
            {
                if (px[i + 3] < 32) continue;
                opaque++;
                int key = px[i + 2] << 16 | px[i + 1] << 8 | px[i];
                all[key] = all.GetValueOrDefault(key) + 1;
            }
            // コマの背景なら左右の縁にも出ているはず（字が 1 色だけのとき、字を背景と見誤らないように）
            var sides = new Dictionary<int, int>();
            int sideOpaque = 0;
            for (int y = 0; y < end; y++)
                foreach (var x in new[] { 0, w - 1 })
                {
                    int i = (y * w + x) * 4;
                    if (px[i + 3] < 32) continue;
                    sideOpaque++;
                    int key = px[i + 2] << 16 | px[i + 1] << 8 | px[i];
                    sides[key] = sides.GetValueOrDefault(key) + 1;
                }
            if (all.Count > 1 && all.MaxBy(kv => kv.Value) is var a && a.Value * 100 >= opaque * 35 && !bg.Contains(Rgb(a.Key))
                && ((sideOpaque > 0 && sides.GetValueOrDefault(a.Key) * 2 >= sideOpaque) || all.Count >= 6))
                bg.Add(Rgb(a.Key));
        }
        static (byte, byte, byte) Rgb(int k) => ((byte)(k >> 16), (byte)(k >> 8), (byte)k);
        bool Ink(int i)
        {
            if (px[i + 3] < 32) return false;
            foreach (var (r, g, b) in bg)
                if (Math.Abs(px[i + 2] - r) <= 12 && Math.Abs(px[i + 1] - g) <= 12 && Math.Abs(px[i] - b) <= 12) return false;
            return true;
        }

        // 行ごとの字の量と、字のある行のかたまり（帯）
        var prof = new double[end];
        var bands = new List<(int Top, int Bottom)>(); // Bottom は含まない
        int start = -1;
        for (int y = 0; y < end; y++)
        {
            int c = 0;
            for (int x = 0; x < w; x++) if (Ink((y * w + x) * 4)) c++;
            prof[y] = c;
            if (c > 0 && start < 0) start = y;
            if (c == 0 && start >= 0) { bands.Add((start, y)); start = -1; }
        }
        if (start >= 0) bands.Add((start, end));
        LastBands = bands;
        LastBackground = bg;

        string Rest(int iv, int n) => h - n * iv > 0 ? $"。下の {h - n * iv}px は使わない余り" : "";

        // 1. 帯がどれも 1 コマに 1 つずつ同じ位置に入る高さ（高さがコマ数で割り切れなくてもよい）。
        //    いくつも当てはまるとき（コマが 2〜3 つしかない画像など）は、帯の位置がいちばんそろうもの。画像の高さを割り切れるものを少し優先する
        if (bands.Count >= 2)
        {
            int minIv = Math.Max(3, bands.Max(b => b.Bottom - b.Top));
            (int Iv, double Cost)? bestStrict = null;
            for (int iv = end; iv >= minIv; iv--)
            {
                var frames = new HashSet<int>();
                bool ok = true;
                double cMin = double.MaxValue, cMax = double.MinValue;
                foreach (var (t, b) in bands)
                {
                    int f0 = t / iv, f1 = (b - 1) / iv;
                    if (f0 != f1 || !frames.Add(f0)) { ok = false; break; }
                    double c = (t + b) / 2.0 - f0 * iv;
                    cMin = Math.Min(cMin, c); cMax = Math.Max(cMax, c);
                }
                if (!ok || cMax - cMin > Math.Max(2, iv * 0.25)) continue;
                double cost = (cMax - cMin) / iv + (h % iv == 0 ? 0 : 0.05);
                if (bestStrict == null || cost < bestStrict.Value.Cost - 1e-9) bestStrict = (iv, cost);
            }
            if (bestStrict is { Iv: var biv })
            {
                int n = h / biv;
                return new DigitalFramesResult(biv, n,
                    $"1 コマ {biv}px・{n} コマ（字のある所 {bands.Count} か所が、どれも 1 コマに 1 つずつ同じ位置にあります{Rest(biv, n)}）", Confident: bands.Count >= 3);
            }
        }

        // 2. くり返し（周期）: 行ごとの字の量をずらして重ねたときの似ている度合い。描かれている所がコマの数で終わるものを優先
        if (end >= 6)
        {
            double mean = prof.Average();
            var d = prof.Select(v => v - mean).ToArray();
            double var0 = d.Sum(v => v * v);
            if (var0 > 0)
            {
                int maxK = end / 2;
                var corr = new double[maxK + 2];
                for (int k = 1; k <= maxK + 1 && k < end; k++)
                {
                    double s = 0, a2 = 0, b2 = 0;
                    for (int y = 0; y + k < end; y++) { s += d[y] * d[y + k]; a2 += d[y] * d[y]; b2 += d[y + k] * d[y + k]; }
                    corr[k] = a2 > 0 && b2 > 0 ? s / Math.Sqrt(a2 * b2) : 0;
                }
                var peaks = new List<(int K, double Score, double Corr)>();
                for (int k = 3; k <= maxK; k++)
                {
                    if (corr[k] < 0.25 || corr[k] < corr[k - 1] || corr[k] < corr[k + 1]) continue;
                    int r = end % k;
                    double misfit = Math.Min(r, k - r) / (double)k;
                    peaks.Add((k, corr[k] - misfit, corr[k]));
                }
                if (peaks.Count > 0)
                {
                    double best = peaks.Max(p => p.Score);
                    // ほぼ同じ点数なら小さい方（2 コマぶんの高さを選ばないように）
                    var pick = peaks.Where(p => p.Score >= best - 0.08).MinBy(p => p.K);
                    int n = h / pick.K;
                    return new DigitalFramesResult(pick.K, n,
                        $"1 コマ {pick.K}px・{n} コマと推定しました（字の並びのくり返しから。違っていたら直してください{Rest(pick.K, n)}）", Confident: false);
                }
            }
        }

        // 3. よくあるコマ数で割り切れるもの
        foreach (var n in new[] { 10, 11, 12, 13, 14, 16, 20, 2, 3, 4, 5, 6, 7, 8, 9 })
            if (end % n == 0 && end / n >= 3)
                return new DigitalFramesResult(end / n, h / (end / n), $"高さ {end} を {n} コマとみなしました（数字の間のすき間もくり返しも見つからないので、よくあるコマ数で割り切れるものを選びました。違っていたら直してください）", Confident: false);
        return null;
    }
}
