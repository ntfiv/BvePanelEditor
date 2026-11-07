using BvePanelEditor.Core;
using BvePanelEditor.Core.Psd;
using Xunit;

namespace BvePanelEditor.Tests;

/// <summary>デジタル数字のコマ割を作る・調べる。</summary>
public sealed class DigitStripTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "panelkun_digit_" + Guid.NewGuid().ToString("N"));
    public DigitStripTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static PsdPixels Solid(int w, int h, byte v) => new(new PsdRect(0, 0, w, h), Enumerable.Range(0, w * h * 4).Select(i => i % 4 == 3 ? (byte)255 : v).ToArray());
    private static byte A(PsdPixels p, int x, int y) => p.Bgra[(y * p.Width + x) * 4 + 3];
    private static byte B(PsdPixels p, int x, int y) => p.Bgra[(y * p.Width + x) * 4];

    [Fact]
    public void PanelSizeIsPowerOfTwo()
    {
        Assert.True(DigitStrip.IsPanelSize(32, 512));
        Assert.False(DigitStrip.IsPanelSize(30, 512));
        Assert.False(DigitStrip.IsPanelSize(32, 320));
    }

    [Fact]
    public void StackPutsFramesTopLeftInLargestCell()
    {
        var (strip, interval) = DigitStrip.Stack([Solid(10, 20, 1), null, Solid(12, 16, 3)]);
        Assert.Equal(20, interval);
        Assert.Equal((12, 60), (strip.Width, strip.Height));
        Assert.Equal(1, B(strip, 0, 0));
        Assert.Equal(0, A(strip, 11, 0));          // 1 コマ目は幅 10 なので右は透明
        Assert.Equal(0, A(strip, 0, 25));          // 2 コマ目は透明なコマ
        Assert.Equal(3, B(strip, 11, 40));         // 3 コマ目は左上（40 行目から）
        Assert.Equal(0, A(strip, 0, 58));          // 3 コマ目は高さ 16 なので下は透明
        Assert.True(DigitStrip.IsTransparent(DigitStrip.Frame(strip, interval, 1)));
        Assert.False(DigitStrip.IsTransparent(DigitStrip.Frame(strip, interval, 2)));
        Assert.Equal((3, 0), DigitStrip.Count(60, 20));
        Assert.Equal((2, 4), DigitStrip.Count(44, 20));
    }

    [Fact]
    public void PsdLayersBecomeFramesIncludingTransparentOnes()
    {
        var path = Path.Combine(_dir, "digits.psd");
        TestPsdWriter.Write(path, 40, 40,
        [
            TestLayer.Solid("2", new PsdRect(5, 5, 15, 25), 20, 20, 20),
            new TestLayer { Name = "空", Bounds = default, Bgra = [] },            // 完全に透明なレイヤー
            TestLayer.Solid("0", new PsdRect(5, 5, 15, 25), 0, 0, 0),
            TestLayer.Solid("1", new PsdRect(6, 5, 16, 25), 10, 10, 10),
        ]);
        using var psd = PsdFile.Open(path);
        var layers = DigitStrip.FrameLayers(psd);
        Assert.Equal(4, layers.Count);                                          // 透明なレイヤーもコマにできる
        // 名前が数字でないものがあるので、Photoshop の上から順
        Assert.Equal(["1", "0", "空", "2"], DigitStrip.FrameOrder(layers).Select(l => l.Name));
        // 全部数字なら数の順
        Assert.Equal(["0", "1", "2"], DigitStrip.FrameOrder(layers.Where(l => l.Name != "空")).Select(l => l.Name));
        // 末尾が数字（motor_3 など）でも数の順
        var named = new[] { "motor_3", "motor_2", "motor_10", "motor_0" }.Select((n, i) => new PsdLayer { Name = n, Index = i }).ToList();
        Assert.Equal(["motor_0", "motor_2", "motor_3", "motor_10"], DigitStrip.FrameOrder(named).Select(l => l.Name));
        // 数が重なれば上から順
        var dup = new[] { "a1", "b1" }.Select((n, i) => new PsdLayer { Name = n, Index = i }).ToList();
        Assert.Equal(["b1", "a1"], DigitStrip.FrameOrder(dup).Select(l => l.Name));
        var w = new List<string>();
        var num = PsdImport.RenderNumber(psd, null, DigitStrip.FrameOrder(layers).Select(l => l.Path).ToList(), w);
        Assert.NotNull(num);
        Assert.Equal(4, num!.Frames);
        Assert.Equal(20, num.Day.Height / 4);                                    // 全コマの範囲（高さ 20）
        Assert.True(DigitStrip.IsTransparent(DigitStrip.Frame(num.Day, 20, 2))); // 透明なコマ
    }
}
