using System.Text;
using BvePanelEditor.Core.Psd;
using Xunit;

namespace BvePanelEditor.Tests;

/// <summary>フォルダのマスク・描画モード・フォルダへのクリッピング・べた塗り・調整レイヤー。</summary>
public sealed class PsdComposeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "panelkun_compose_" + Guid.NewGuid().ToString("N"));
    public PsdComposeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    static readonly PsdRect Full = new(0, 0, 8, 4);

    private PsdFile Open(params TestLayer[] layers)
    {
        var p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".psd");
        TestPsdWriter.Write(p, 8, 4, layers);
        return PsdFile.Open(p);
    }

    private static (int R, int G, int B, int A) At(PsdPixels px, int x, int y)
    {
        int i = ((y - px.Bounds.Top) * px.Width + (x - px.Bounds.Left)) * 4;
        return (px.Bgra[i + 2], px.Bgra[i + 1], px.Bgra[i], px.Bgra[i + 3]);
    }

    private static PsdPixels All(PsdFile psd) => PsdCompose.Compose(psd, psd.Layers, psd.Canvas);

    /// <summary>左半分が 255、右半分が 0 のマスク。</summary>
    private static byte[] LeftHalf()
    {
        var m = new byte[8 * 4];
        for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) m[y * 8 + x] = 255;
        return m;
    }

    private static TestLayer Adjustment(string name, string key, byte[] data) =>
        new() { Name = name, Bounds = default, Bgra = [], Blocks = [(key, data)] };

    private static byte[] Be(params int[] u16) => u16.SelectMany(v => new[] { (byte)(v >> 8), (byte)v }).ToArray();

    [Fact]
    public void FolderMaskLimitsChildren()
    {
        using var psd = Open(
            TestLayer.Solid("板", Full, 100, 100, 100),
            TestLayer.FolderEnd(),
            TestLayer.Solid("赤", Full, 255, 0, 0),
            new TestLayer { Name = "夜", SectionType = 1, MaskBounds = Full, Mask = LeftHalf() });
        Assert.NotNull(psd.GetMask(psd.Find("夜")!));
        var img = All(psd);
        Assert.Equal((255, 0, 0, 255), At(img, 1, 1));
        Assert.Equal((100, 100, 100, 255), At(img, 6, 1));
    }

    [Theory]
    [InlineData("diff", 128, 200, 72)]
    [InlineData("fsub", 128, 200, 0)]
    [InlineData("fsub", 200, 128, 72)]
    [InlineData("smud", 0, 200, 200)]
    [InlineData("lbrn", 200, 100, 45)]
    [InlineData("idiv", 255, 10, 255)]
    [InlineData("pLit", 200, 50, 100)]
    [InlineData("hMix", 200, 100, 255)]
    [InlineData("fdiv", 100, 200, 128)]
    public void SeparableBlendModes(string mode, int bottom, int top, int expected)
    {
        using var psd = Open(
            TestLayer.Solid("下", Full, (byte)bottom, (byte)bottom, (byte)bottom),
            WithBlend(TestLayer.Solid("上", Full, (byte)top, (byte)top, (byte)top), mode));
        Assert.True(PsdCompose.IsSupportedBlend(mode));
        var (r, _, _, _) = At(All(psd), 2, 2);
        Assert.InRange(r, expected - 1, expected + 1);
    }

    private static TestLayer WithBlend(TestLayer l, string mode) { l.Blend = mode; return l; }

    [Fact]
    public void LuminosityAndColorModes()
    {
        using var psd = Open(
            TestLayer.Solid("赤", Full, 255, 0, 0),
            WithBlend(TestLayer.Solid("灰", Full, 128, 128, 128), "lum "));
        var (r, g, b, _) = At(All(psd), 0, 0);
        Assert.Equal(255, r);
        Assert.InRange(g, 70, 76);
        Assert.Equal(g, b);

        using var sat = Open(TestLayer.Solid("赤", Full, 255, 0, 0), WithBlend(TestLayer.Solid("灰", Full, 128, 128, 128), "sat "));
        var s = At(All(sat), 0, 0);
        Assert.Equal(s.R, s.G);  // 彩度 0 の色で「彩度」→ 灰色
    }

    [Fact]
    public void ClipToFolder()
    {
        var small = new PsdRect(2, 1, 4, 3);
        using var psd = Open(
            TestLayer.FolderEnd(),
            TestLayer.Solid("四角", small, 255, 255, 255),
            TestLayer.FolderStart("形"),
            WithClip(TestLayer.Solid("青", Full, 0, 0, 255)));
        var img = All(psd);
        Assert.Equal((0, 0, 255, 255), At(img, 2, 1));
        Assert.Equal(0, At(img, 6, 1).A);
    }

    private static TestLayer WithClip(TestLayer l) { l.Clipped = true; return l; }

    private static byte[] SolidColorBlock(double r, double g, double b)
    {
        var ms = new MemoryStream();
        void U32(uint v) { ms.Write([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]); }
        void Key(string k) { U32(0); ms.Write(Encoding.ASCII.GetBytes(k)); }
        void Dbl(double d) { var x = BitConverter.GetBytes(d); Array.Reverse(x); ms.Write(x); }
        U32(16);                        // 版
        U32(1); ms.Write([0, 0]);       // 名前（空）
        Key("null"); U32(1);
        Key("Clr "); ms.Write(Encoding.ASCII.GetBytes("Objc"));
        U32(1); ms.Write([0, 0]); Key("RGBC"); U32(3);
        Key("Rd  "); ms.Write(Encoding.ASCII.GetBytes("doub")); Dbl(r);
        Key("Grn "); ms.Write(Encoding.ASCII.GetBytes("doub")); Dbl(g);
        Key("Bl  "); ms.Write(Encoding.ASCII.GetBytes("doub")); Dbl(b);
        return ms.ToArray();
    }

    [Fact]
    public void SolidFillWithMask()
    {
        using var psd = Open(
            TestLayer.Solid("板", Full, 0, 0, 0),
            new TestLayer { Name = "べた塗り", Bgra = [], Blocks = [("SoCo", SolidColorBlock(10, 20, 30))], MaskBounds = Full, Mask = LeftHalf() });
        var fill = psd.Find("べた塗り")!;
        Assert.Equal(PsdLayerKind.Fill, fill.Kind);
        Assert.Equal(((byte)10, (byte)20, (byte)30), fill.SolidColor);
        Assert.True(fill.CanCompose);
        var img = All(psd);
        Assert.Equal((10, 20, 30, 255), At(img, 1, 1));
        Assert.Equal((0, 0, 0, 255), At(img, 6, 1));
    }

    [Fact]
    public void LevelsInvertWithMask()
    {
        var levl = new List<int> { 2, 0, 255, 255, 0, 100 };
        for (int k = 1; k < 29; k++) levl.AddRange([0, 255, 0, 255, 100]);
        using var psd = Open(
            TestLayer.Solid("板", Full, 50, 50, 50),
            new TestLayer { Name = "レベル", Bgra = [], Blocks = [("levl", Be([.. levl]))], MaskBounds = Full, Mask = LeftHalf() });
        var l = psd.Find("レベル")!;
        Assert.Equal(PsdLayerKind.Adjustment, l.Kind);
        Assert.Equal("レベル補正", l.Adjustment!.Name);
        Assert.Equal(PsdRole.Base, PsdImport.DefaultRole(l, psd.Canvas));
        var img = All(psd);
        Assert.Equal((205, 205, 205, 255), At(img, 1, 1));
        Assert.Equal((50, 50, 50, 255), At(img, 6, 1));
    }

    [Fact]
    public void CurvesInvert()
    {
        var curv = new byte[] { 0 }.Concat(Be(1, 0, 1, 2, 255, 0, 0, 255)).ToArray();  // 版 1、RGB だけ、点 (入力 0→出力 255)・(255→0)
        using var psd = Open(TestLayer.Solid("板", Full, 50, 100, 200), Adjustment("カーブ", "curv", curv));
        var (r, g, b, _) = At(All(psd), 0, 0);
        Assert.Equal((205, 155, 55), (r, g, b));
    }

    [Fact]
    public void LegacyBrightnessAndOpacity()
    {
        var brit = Be(51, 0, 127).Concat(new byte[] { 0 }).ToArray();
        var adj = Adjustment("明るさ", "brit", brit);
        adj.Opacity = 128;
        using var psd = Open(TestLayer.Solid("板", Full, 100, 100, 100), adj);
        var (r, _, _, _) = At(All(psd), 0, 0);
        Assert.InRange(r, 124, 127); // 100 + 51 × 半分
    }

    [Fact]
    public void HueSaturationDesaturates()
    {
        var hue = new byte[100];
        Be(2).CopyTo(hue, 0);
        Be(unchecked((ushort)-100)).CopyTo(hue, 12); // マスターの彩度 -100
        using var psd = Open(TestLayer.Solid("板", Full, 200, 50, 50), Adjustment("色相", "hue2", hue));
        var (r, g, b, _) = At(All(psd), 0, 0);
        Assert.Equal(r, g);
        Assert.Equal(g, b);
        Assert.InRange(r, 120, 130);
    }

    [Fact]
    public void UnsupportedAdjustmentIsNotComposable()
    {
        using var psd = Open(TestLayer.Solid("板", Full, 100, 100, 100), Adjustment("露光量", "expA", new byte[16]));
        var l = psd.Find("露光量")!;
        Assert.Equal(PsdLayerKind.Adjustment, l.Kind);
        Assert.False(l.CanCompose);
        Assert.Contains("露光量", PsdImport.Notes(l));
        Assert.Equal((100, 100, 100, 255), At(All(psd), 0, 0));
    }

    [Fact]
    public void SplineThroughPoints()
    {
        var lut = PsdAdjust.Spline([(0, 0), (128, 200), (255, 255)]);
        Assert.InRange(lut[128] * 255, 199, 201);
        Assert.Equal(0, lut[0]);
        Assert.Equal(1, lut[255]);
    }
}
