using BvePanelEditor.Core;
using BvePanelEditor.Core.Psd;
using Xunit;

namespace BvePanelEditor.Tests;

/// <summary>デジタル数字の画像から 1 コマの高さを見つける。</summary>
public class DigitalFramesTests
{
    /// <summary>
    /// 幅 w、1 コマ iv の高さで n コマ並べた画像。各コマの glyphTop〜glyphBottom に字（四角）を描く。blank のコマは空。
    /// bg が null なら透明、あれば全体をその色で塗る。
    /// </summary>
    private static PsdPixels Strip(int w, int iv, int n, int glyphTop, int glyphBottom, int[]? blank = null, (byte, byte, byte)? bg = null, int extraRows = 0)
    {
        int h = iv * n + extraRows;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (bg is { } c) { px[i] = c.Item3; px[i + 1] = c.Item2; px[i + 2] = c.Item1; px[i + 3] = 255; }
                int f = y / iv, r = y % iv;
                if (f >= n || (blank?.Contains(f) ?? false)) continue;
                // 字の形をコマごとに少し変える（幅）
                int x0 = 2 + f % 3, x1 = w - 2 - f % 2;
                if (r >= glyphTop && r < glyphBottom && x >= x0 && x < x1) { px[i] = 40; px[i + 1] = 220; px[i + 2] = 255; px[i + 3] = 255; }
            }
        return new PsdPixels(new PsdRect(0, 0, w, h), px);
    }

    [Fact]
    public void TransparentGaps()
    {
        var r = DigitalFrames.Detect(Strip(16, 24, 10, 3, 21))!;
        Assert.Equal((24, 10), (r.Interval, r.Frames));
        Assert.True(r.Confident);
    }

    [Fact]
    public void BlankFrameAndOddCount()
    {
        // 11 コマ（0〜9 と空白）。最後のコマは空
        var r = DigitalFrames.Detect(Strip(16, 20, 11, 4, 16, blank: [10]))!;
        Assert.Equal((20, 11), (r.Interval, r.Frames));
    }

    [Fact]
    public void SolidBackgroundWithGaps()
    {
        var r = DigitalFrames.Detect(Strip(16, 30, 12, 5, 25, bg: (10, 10, 10)))!;
        Assert.Equal((30, 12), (r.Interval, r.Frames));
    }

    [Fact]
    public void PaddedToPow2UsesContentHeight()
    {
        // 32×10 = 320 を 512 に広げた PNG（下は透明）
        var img = Strip(16, 32, 10, 2, 30, extraRows: 192);
        Assert.Equal((32, 10), (DigitalFrames.Detect(img, height: 320)!.Interval, 10));
    }

    [Fact]
    public void NoGapsFallsBackToCommonCounts()
    {
        // 字がコマいっぱい（すき間なし）: 画像だけでは決められないので、推定（Confident = false）として返す
        var r = DigitalFrames.Detect(Strip(16, 20, 10, 0, 20))!;
        Assert.False(r.Confident);
    }

    [Fact]
    public void TwoFramesPreferEvenSplit()
    {
        // 2 コマ（切／入）の 512×1024 のような画像。下のコマの絵は少し下寄り。割り切れる 512 を選ぶ
        var img = Strip(16, 512, 2, 16, 497);
        for (int y = 512 + 15; y < 512 + 30; y++) for (int x = 4; x < 12; x++) { int i = (y * 16 + x) * 4; img.Bgra[i + 3] = 255; img.Bgra[i + 2] = 255; }
        Assert.Equal(512, DigitalFrames.Detect(img)!.Interval);
    }

    [Fact]
    public void NotDivisible()
    {
        Assert.Null(DigitalFrames.Detect(Strip(8, 1, 1, 0, 1, extraRows: 0)));
    }
}
