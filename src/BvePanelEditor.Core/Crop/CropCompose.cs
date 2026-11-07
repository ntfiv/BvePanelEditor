using BvePanelEditor.Core.Psd;

namespace BvePanelEditor.Core.Crop;

/// <summary>切り抜く元の透明な所に、メインパネル（無発光）を重ねる。</summary>
public static class CropCompose
{
    /// <summary>少しでも透明（不透明でない）画素があるか。</summary>
    public static bool HasTransparency(PsdPixels img)
    {
        for (int i = 3; i < img.Bgra.Length; i += 4) if (img.Bgra[i] != 255) return true;
        return false;
    }

    /// <summary>top を under の上に重ねた絵（大きさ・位置は top と同じ）。under は (ux, uy) に置く。</summary>
    public static PsdPixels Over(PsdPixels under, PsdPixels top, int ux = 0, int uy = 0)
    {
        int w = top.Width, h = top.Height;
        var acc = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int cy = top.Bounds.Top + y, sy = cy - uy - under.Bounds.Top;
            for (int x = 0; x < w; x++)
            {
                int cx = top.Bounds.Left + x, sx = cx - ux - under.Bounds.Left;
                int di = (y * w + x) * 4;
                if (sy >= 0 && sy < under.Height && sx >= 0 && sx < under.Width)
                    Buffer.BlockCopy(under.Bgra, (sy * under.Width + sx) * 4, acc, di, 4);
                int sa = top.Bgra[di + 3];
                if (sa == 0) continue;
                int da = acc[di + 3], oa = sa + da * (255 - sa) / 255;
                for (int c = 0; c < 3; c++)
                    acc[di + c] = (byte)(oa == 0 ? 0 : (top.Bgra[di + c] * sa + acc[di + c] * da * (255 - sa) / 255) / oa);
                acc[di + 3] = (byte)oa;
            }
        }
        return new PsdPixels(top.Bounds, acc);
    }
}
