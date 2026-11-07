using BvePanelEditor.Core;
using BvePanelEditor.Core.Psd;
using Xunit;

namespace BvePanelEditor.Tests;

/// <summary>パネル本体の描かれている範囲を見つける。</summary>
public class PanelBoundsTests
{
    /// <summary>bg で塗った w×h の中に、content の範囲だけ模様（市松）を描いた画像。</summary>
    private static PsdPixels Image(int w, int h, (byte R, byte G, byte B, byte A) bg, PsdRect content)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool inside = x >= content.Left && x < content.Right && y >= content.Top && y < content.Bottom;
                if (inside)
                {
                    byte v = (byte)(((x / 4 + y / 4) % 2 == 0) ? 90 : 160);
                    px[i] = v; px[i + 1] = (byte)(v / 2); px[i + 2] = 200; px[i + 3] = 255;
                }
                else { px[i] = bg.B; px[i + 1] = bg.G; px[i + 2] = bg.R; px[i + 3] = bg.A; }
            }
        return new PsdPixels(new PsdRect(0, 0, w, h), px);
    }

    [Fact]
    public void TransparentAround()
    {
        var r = PanelBounds.Detect(Image(200, 100, (0, 0, 0, 0), new PsdRect(20, 10, 180, 90)));
        Assert.Equal(new PsdRect(20, 10, 180, 90), r.Content);
        Assert.True(r.TransparentEdge);
        Assert.Empty(r.Background);
    }

    [Fact]
    public void SolidFillAround()
    {
        var img = Image(200, 100, (0, 0, 0, 255), new PsdRect(30, 5, 170, 95));
        var r = PanelBounds.Detect(img);
        Assert.Equal(new PsdRect(30, 5, 170, 95), r.Content);
        Assert.Equal((0, 0, 0), Assert.Single(r.Background));
        // 一定の色を余白とみなさなければ、画像全体
        Assert.Equal(new PsdRect(0, 0, 200, 100), PanelBounds.Detect(img, useFillColor: false).Content);
    }

    [Fact]
    public void ToleranceAndSpecks()
    {
        var img = Image(200, 100, (10, 10, 10, 255), new PsdRect(40, 20, 160, 80));
        // 余白の色に少しむらがある・離れた所に 1 点だけ汚れがある
        for (int x = 0; x < 200; x += 3) { int i = (2 * 200 + x) * 4; img.Bgra[i] = 16; img.Bgra[i + 1] = 14; img.Bgra[i + 2] = 15; }
        int s = (95 * 200 + 5) * 4; img.Bgra[s] = 255; img.Bgra[s + 1] = 255; img.Bgra[s + 2] = 255;
        Assert.Equal(new PsdRect(40, 20, 160, 80), PanelBounds.Detect(img).Content);
        // 許容差 0 だとむらを描かれているとみなす
        Assert.Equal(2, PanelBounds.Detect(img, tolerance: 0).Content.Top);
    }

    [Fact]
    public void ShrinkAndOverlap()
    {
        var a = new PsdRect(10, 10, 110, 60);
        Assert.Equal(new PsdRect(15, 15, 105, 55), PanelBounds.Shrink(a, 5));
        Assert.True(PanelBounds.Shrink(a, 30).IsEmpty);
        Assert.Equal(new PsdRect(20, 10, 110, 50), PanelBounds.Overlap(a, new PsdRect(20, 0, 200, 50)));
        Assert.Equal(a, PanelBounds.Overlap(a, default));
    }

    [Fact]
    public void NothingDrawn()
    {
        var r = PanelBounds.Detect(Image(50, 50, (0, 0, 0, 0), default));
        Assert.True(r.Content.IsEmpty);
    }
}
