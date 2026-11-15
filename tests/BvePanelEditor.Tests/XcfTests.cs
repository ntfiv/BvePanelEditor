using BvePanelEditor.Core.Psd;
using BvePanelEditor.Core.Xcf;
using Xunit;

namespace BvePanelEditor.Tests;

public sealed class XcfTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "panelkun_xcf_" + Guid.NewGuid().ToString("N"));

    public XcfTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string WriteXcf(string name, int w, int h, IReadOnlyList<TestXcfLayer> layersTopFirst,
        int version = 3, int compression = 1, int baseType = 0, int precision = 0)
    {
        var p = Path.Combine(_dir, name);
        TestXcfWriter.Write(p, w, h, layersTopFirst, version, compression, baseType, precision);
        return p;
    }

    private static (byte R, byte G, byte B, byte A) At(PsdPixels px, int cx, int cy)
    {
        int i = ((cy - px.Bounds.Top) * px.Width + (cx - px.Bounds.Left)) * 4;
        return (px.Bgra[i + 2], px.Bgra[i + 1], px.Bgra[i], px.Bgra[i + 3]);
    }

    [Fact]
    public void SingleRgbaLayerReadsBack()
    {
        var layer = TestXcfLayer.Solid("板", 2, 2, 10, 20, 30, 40);
        layer.OffsetX = 1;
        layer.OffsetY = 0;
        layer.Opacity = 200;
        var p = WriteXcf("one.xcf", 4, 2, [layer]);
        using var xcf = XcfFile.Open(p);
        Assert.Equal(4, xcf.Width);
        Assert.Equal(2, xcf.Height);
        var l = Assert.Single(xcf.PixelLayers);
        Assert.Equal("板", l.Name);
        Assert.Equal(new PsdRect(1, 0, 3, 2), l.Bounds);
        Assert.Equal((byte)200, l.Opacity);
        Assert.False(l.Hidden);
        var px = xcf.GetPixels(l);
        Assert.NotNull(px);
        Assert.Equal(((byte)10, (byte)20, (byte)30, (byte)40), At(px!, 1, 0));
    }

    [Fact]
    public void CompressionVariantsGiveSamePixels()
    {
        // 70×70（タイル境界をまたぐ大きさ）で、圧縮 0/1/2 が同じ絵になること
        var src = new TestXcfLayer { Name = "模様", Width = 70, Height = 70, Type = 1 };
        src.Pixels = new byte[70 * 70 * 4];
        for (int i = 0; i < 70 * 70; i++)
        {
            src.Pixels[i * 4] = (byte)(i % 251);
            src.Pixels[i * 4 + 1] = (byte)(i / 70 % 256);
            src.Pixels[i * 4 + 2] = (byte)(i * 7 % 256);
            src.Pixels[i * 4 + 3] = (byte)(200 + i % 56);
        }
        byte[]? reference = null;
        foreach (int comp in new[] { 0, 1, 2 })
        {
            var p = WriteXcf($"comp{comp}.xcf", 70, 70, [src], compression: comp);
            using var xcf = XcfFile.Open(p);
            var l = Assert.Single(xcf.PixelLayers);
            var px = xcf.GetPixels(l);
            Assert.NotNull(px);
            if (reference == null) reference = px!.Bgra;
            else Assert.Equal(reference, px!.Bgra);
            int i = 69 * 70;
            Assert.Equal(((byte)(i % 251), (byte)(i / 70 % 256), (byte)(i * 7 % 256), (byte)(200 + i % 56)), At(px!, 0, 69));
        }
    }

    [Fact]
    public void GrayLayersExpandToRgb()
    {
        var ga = new TestXcfLayer { Name = "グレーα", Width = 2, Height = 1, Type = 3, Pixels = [100, 255, 50, 128] };
        var g = new TestXcfLayer { Name = "グレー", Width = 1, Height = 1, Type = 2, Pixels = [77] };
        var p = WriteXcf("gray.xcf", 2, 1, [ga, g], baseType: 1);
        using var xcf = XcfFile.Open(p);
        var top = xcf.Find("グレーα");
        Assert.NotNull(top);
        var px = xcf.GetPixels(top!);
        Assert.Equal(((byte)100, (byte)100, (byte)100, (byte)255), At(px!, 0, 0));
        Assert.Equal((byte)128, At(px!, 1, 0).A);
        var bottom = xcf.Find("グレー");
        var px2 = xcf.GetPixels(bottom!);
        Assert.Equal(((byte)77, (byte)77, (byte)77, (byte)255), At(px2!, 0, 0));
    }

    [Fact]
    public void GroupTreeIsBuiltFromItemPaths()
    {
        var group = new TestXcfLayer { Name = "束", IsGroup = true, ItemPath = [0], Closed = true };
        var a = TestXcfLayer.Solid("a", 2, 2, 1, 2, 3);
        a.ItemPath = [0, 0];
        var b = TestXcfLayer.Solid("b", 2, 2, 4, 5, 6);
        b.ItemPath = [0, 1];
        var c = TestXcfLayer.Solid("c", 2, 2, 7, 8, 9);
        c.ItemPath = [1];
        var p = WriteXcf("group.xcf", 2, 2, [group, a, b, c], version: 11);
        using var xcf = XcfFile.Open(p);
        Assert.Equal(
            [PsdLayerKind.Pixel, PsdLayerKind.FolderEnd, PsdLayerKind.Pixel, PsdLayerKind.Pixel, PsdLayerKind.FolderOpen],
            xcf.Layers.Select(l => l.Kind).ToArray());
        var folder = xcf.Layers[^1];
        Assert.Equal("束", folder.Name);
        Assert.True(folder.Closed);
        Assert.Equal("束/a", xcf.Find("束/a")?.Path);
        Assert.Equal("束/b", xcf.Find("束/b")?.Path);
        Assert.Same(folder, xcf.Find("束/a")?.Parent);
        Assert.Equal("c", xcf.Layers[0].Path);
    }

    [Fact]
    public void MaskMultipliesAlpha()
    {
        var layer = TestXcfLayer.Solid("マスク付き", 2, 1, 9, 9, 9, 200);
        layer.Mask = [255, 0];
        var p = WriteXcf("mask.xcf", 2, 1, [layer]);
        using var xcf = XcfFile.Open(p);
        var l = Assert.Single(xcf.PixelLayers);
        Assert.True(l.HasMask);
        var px = xcf.GetPixels(l);
        Assert.Equal((byte)200, At(px!, 0, 0).A);
        Assert.Equal((byte)0, At(px!, 1, 0).A);
        Assert.NotNull(xcf.GetMask(l));

        var off = TestXcfLayer.Solid("マスク無効", 2, 1, 9, 9, 9, 200);
        off.Mask = [255, 0];
        off.ApplyMask = false;
        var p2 = WriteXcf("maskoff.xcf", 2, 1, [off]);
        using var xcf2 = XcfFile.Open(p2);
        var l2 = Assert.Single(xcf2.PixelLayers);
        var px2 = xcf2.GetPixels(l2);
        Assert.Equal((byte)200, At(px2!, 1, 0).A);
        Assert.Null(xcf2.GetMask(l2));
    }

    [Fact]
    public void BlendModesMapToPsdKeys()
    {
        Assert.Equal("mul ", XcfFile.MapBlendMode(30, out _, out bool u1));
        Assert.False(u1);
        Assert.Equal("pass", XcfFile.MapBlendMode(61, out _, out _));
        Assert.Equal("lum ", XcfFile.MapBlendMode(40, out bool approx, out _));
        Assert.True(approx);
        XcfFile.MapBlendMode(47, out _, out bool unsupported); // グレイン統合
        Assert.True(unsupported);

        var layer = TestXcfLayer.Solid("統合", 1, 1, 1, 1, 1);
        layer.Mode = 47;
        var p = WriteXcf("mode.xcf", 1, 1, [layer]);
        using var xcf = XcfFile.Open(p);
        var l = Assert.Single(xcf.PixelLayers);
        Assert.Equal("norm", l.BlendMode);
        Assert.True(xcf.LayerInfo[l].ModeUnsupported);
        Assert.Equal(47, xcf.LayerInfo[l].Mode);
    }

    [Fact]
    public void Version11Uses64BitPointers()
    {
        var layer = TestXcfLayer.Solid("新", 3, 2, 5, 6, 7, 8);
        var p = WriteXcf("v11.xcf", 3, 2, [layer], version: 11);
        using var xcf = XcfFile.Open(p);
        Assert.Equal(11, xcf.Version);
        var l = Assert.Single(xcf.PixelLayers);
        var px = xcf.GetPixels(l);
        Assert.Equal(((byte)5, (byte)6, (byte)7, (byte)8), At(px!, 2, 1));
    }

    [Fact]
    public void RejectsIndexedAndHighPrecisionAndNonXcf()
    {
        var layer = TestXcfLayer.Solid("x", 1, 1, 0, 0, 0);
        var indexed = WriteXcf("idx.xcf", 1, 1, [layer], baseType: 2);
        Assert.Throws<NotSupportedException>(() => XcfFile.Open(indexed));
        var deep = WriteXcf("deep.xcf", 1, 1, [layer], version: 11, precision: 2);
        Assert.Throws<NotSupportedException>(() => XcfFile.Open(deep));
        var junk = Path.Combine(_dir, "junk.xcf");
        File.WriteAllBytes(junk, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);
        Assert.Throws<InvalidDataException>(() => XcfFile.Open(junk));
    }

    [Fact]
    public void HiddenOpacityAndTextKind()
    {
        var layer = TestXcfLayer.Solid("文字レイヤー", 2, 2, 1, 1, 1);
        layer.Visible = false;
        layer.IsText = true;
        var p = WriteXcf("text.xcf", 2, 2, [layer]);
        using var xcf = XcfFile.Open(p);
        var l = Assert.Single(xcf.PixelLayers);
        Assert.True(l.Hidden);
        Assert.True(l.EffectivelyHidden);
        Assert.Equal(PsdLayerKind.Text, l.Kind);
    }
}
