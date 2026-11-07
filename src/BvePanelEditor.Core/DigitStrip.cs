using BvePanelEditor.Core.Psd;

namespace BvePanelEditor.Core;

/// <summary>デジタル数字（DigitalNumber）のコマ割の画像を作る・調べる。</summary>
public static class DigitStrip
{
    /// <summary>BVE のパネル用の大きさ（幅・高さとも 2 の累乗）か。</summary>
    public static bool IsPanelSize(int width, int height) =>
        width > 0 && height > 0 && PngWriter.NextPow2(width) == width && PngWriter.NextPow2(height) == height;

    /// <summary>すべて透明か（null も透明）。</summary>
    public static bool IsTransparent(PsdPixels? img)
    {
        if (img == null) return true;
        for (int i = 3; i < img.Bgra.Length; i += 4) if (img.Bgra[i] != 0) return false;
        return true;
    }

    /// <summary>
    /// コマを上から順に縦に並べる。1 コマの大きさは、いちばん大きいコマの幅・高さ（どのコマも左上に詰める。null は透明なコマ）。
    /// 返す Interval は 1 コマの高さ。
    /// </summary>
    public static (PsdPixels Strip, int Interval) Stack(IReadOnlyList<PsdPixels?> frames)
    {
        var (fw, fh) = CellSize(frames);
        return (Stack(frames, fw, fh), fh);
    }

    /// <summary>コマの大きさ（いちばん大きいコマの幅・高さ。昼と夜をそろえるときは両方を渡す）。</summary>
    public static (int Width, int Height) CellSize(IEnumerable<PsdPixels?> frames)
    {
        var list = frames.Where(f => f != null).ToList();
        return (Math.Max(1, list.Select(f => f!.Width).DefaultIfEmpty(1).Max()), Math.Max(1, list.Select(f => f!.Height).DefaultIfEmpty(1).Max()));
    }

    /// <summary>コマを上から順に、1 コマ fw×fh で縦に並べる（どのコマも左上に詰める。null は透明なコマ）。</summary>
    public static PsdPixels Stack(IReadOnlyList<PsdPixels?> frames, int fw, int fh)
    {
        int n = Math.Max(1, frames.Count);
        var bgra = new byte[fw * fh * n * 4];
        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i] is not { } f) continue;
            int cw = Math.Min(f.Width, fw);
            for (int y = 0; y < Math.Min(f.Height, fh); y++)
                Buffer.BlockCopy(f.Bgra, y * f.Width * 4, bgra, ((i * fh + y) * fw) * 4, cw * 4);
        }
        return new PsdPixels(new PsdRect(0, 0, fw, fh * n), bgra);
    }

    /// <summary>縦に並んだ画像から、i 番目のコマ（高さ interval）を切り出す（はみ出した所は透明）。</summary>
    public static PsdPixels Frame(PsdPixels strip, int interval, int index)
    {
        int w = strip.Width, h = Math.Max(1, interval);
        var bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int sy = index * interval + y;
            if (sy < 0 || sy >= strip.Height) continue;
            Buffer.BlockCopy(strip.Bgra, sy * w * 4, bgra, y * w * 4, w * 4);
        }
        return new PsdPixels(new PsdRect(0, 0, w, h), bgra);
    }

    /// <summary>絵のある範囲（不透明度が 0 でない画素を囲む四角。全部透明なら空）。</summary>
    public static PsdRect ContentBounds(PsdPixels img)
    {
        int w = img.Width, h = img.Height, l = w, t = h, r = -1, b = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (img.Bgra[(y * w + x) * 4 + 3] != 0)
                {
                    if (x < l) l = x; if (x > r) r = x;
                    if (y < t) t = y; if (y > b) b = y;
                }
        return r < 0 ? default : new PsdRect(img.Bounds.Left + l, img.Bounds.Top + t, img.Bounds.Left + r + 1, img.Bounds.Top + b + 1);
    }

    /// <summary>コマ数（中身の高さ ÷ 1 コマの高さ。割り切れなければ切り捨て）と、余り。</summary>
    public static (int Frames, int Remainder) Count(int contentHeight, int interval) =>
        interval <= 0 ? (0, contentHeight) : (contentHeight / interval, contentHeight % interval);

    /// <summary>
    /// PSD のレイヤーをコマにする順: 名前の末尾が全部数字（「0」「motor_3」など）で重ならなければ数の小さい順、
    /// そうでなければ Photoshop のレイヤーパネルと同じ上から順。
    /// </summary>
    public static List<PsdLayer> FrameOrder(IEnumerable<PsdLayer> layers)
    {
        var list = layers.ToList();
        var nums = list.Select(l => TrailingNumber(PsdFile.StripDuplicateMark(l.Name))).ToList();
        if (list.Count > 0 && nums.All(v => v != null) && nums.Distinct().Count() == nums.Count)
            return list.Select((l, i) => (l, n: nums[i]!.Value)).OrderBy(t => t.n).Select(t => t.l).ToList();
        return list.OrderByDescending(l => l.Index).ToList();
    }

    /// <summary>名前の末尾の数（「motor_3」→ 3、「12」→ 12）。なければ null。</summary>
    private static long? TrailingNumber(string name)
    {
        var t = name.Trim();
        int i = t.Length;
        while (i > 0 && char.IsAsciiDigit(t[i - 1])) i--;
        return i < t.Length && i > t.Length - 10 && long.TryParse(t[i..], out var v) ? v : null;
    }

    /// <summary>PSD の中で、コマにできるレイヤー（画像・文字・スマートオブジェクト。完全に透明なレイヤーも含む。フォルダや調整レイヤーは除く）。</summary>
    public static List<PsdLayer> FrameLayers(PsdFile psd) =>
        psd.Layers.Where(l => l.Kind is PsdLayerKind.Pixel or PsdLayerKind.Text or PsdLayerKind.Smart).ToList();
}
