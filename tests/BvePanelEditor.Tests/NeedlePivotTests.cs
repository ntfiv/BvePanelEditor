using BvePanelEditor.Core;
using BvePanelEditor.Core.Psd;
using Xunit;

namespace BvePanelEditor.Tests;

/// <summary>針の画像から回転の中心を見つける。</summary>
public class NeedlePivotTests
{
    /// <summary>上向きの針: 細い棒、軸の丸（hubY・半径 hubR、なければ 0）、下に尾（広がる台形、tail 行）。</summary>
    private static PsdPixels Needle(int w, int h, int shaftW, int hubY, int hubR, int tailTop = -1, int tailBottom = -1, int tailW = 0)
    {
        var px = new byte[w * h * 4];
        void Set(int x, int y) { if (x >= 0 && x < w && y >= 0 && y < h) px[(y * w + x) * 4 + 3] = 255; }
        int cx = w / 2;
        int bottom = tailBottom >= 0 ? tailBottom : hubY + hubR;
        for (int y = 2; y <= bottom; y++) for (int x = cx - shaftW / 2; x < cx - shaftW / 2 + shaftW; x++) Set(x, y);
        if (hubR > 0)
            for (int y = hubY - hubR; y <= hubY + hubR; y++)
                for (int x = cx - hubR; x <= cx + hubR; x++)
                    if ((x - cx) * (x - cx) + (y - hubY) * (y - hubY) <= hubR * hubR) Set(x, y);
        if (tailTop >= 0)
            for (int y = tailTop; y <= tailBottom; y++)
            {
                int half = shaftW / 2 + (tailW / 2 - shaftW / 2) * (y - tailTop) / Math.Max(1, tailBottom - tailTop);
                for (int x = cx - half; x <= cx + half; x++) Set(x, y);
            }
        return new PsdPixels(new PsdRect(0, 0, w, h), px);
    }

    [Fact]
    public void HubAtBottom()
    {
        var r = NeedlePivot.Find(Needle(64, 256, 6, 164, 16))!;
        Assert.True(r.Found);
        Assert.Equal(32, r.X);
        Assert.InRange(r.Y, 163, 165);
    }

    [Fact]
    public void HubWithFlaredTailBelow()
    {
        // 尾（下が広がる）の方が丸より広くても、丸を選ぶ
        var r = NeedlePivot.Find(Needle(64, 256, 8, 160, 13, tailTop: 175, tailBottom: 238, tailW: 42))!;
        Assert.True(r.Found);
        Assert.InRange(r.Y, 159, 161);
    }

    [Fact]
    public void NoHubFallsBackToBottom()
    {
        var r = NeedlePivot.Find(Needle(32, 256, 18, 230, 0))!;
        Assert.False(r.Found);
        Assert.InRange(r.Y, 218, 224);
    }

    [Fact]
    public void EmptyImage() => Assert.Null(NeedlePivot.Find(new PsdPixels(new PsdRect(0, 0, 8, 8), new byte[256])));
}
