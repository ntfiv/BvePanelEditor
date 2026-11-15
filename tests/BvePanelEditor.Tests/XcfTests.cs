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
        int version = 3, int compression = 1, int baseType = 0, uint precision = 150,
        IReadOnlyList<(string Name, byte[] Data)>? parasites = null, byte[]? parasitesRaw = null,
        bool badUserUnit = false)
    {
        var p = Path.Combine(_dir, name);
        TestXcfWriter.Write(p, w, h, layersTopFirst, version, compression, baseType, precision, parasites, parasitesRaw, badUserUnit);
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
        var deep = WriteXcf("deep.xcf", 1, 1, [layer], version: 11, precision: 200);
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

    [Fact]
    public void PrecisionByGeneration()
    {
        var layer = TestXcfLayer.Solid("x", 1, 1, 3, 4, 5);
        // 版 3 以前は精度フィールド自体が無い（8bit ガンマ扱い）
        using (var x = XcfFile.Open(WriteXcf("p3.xcf", 1, 1, [layer], version: 3)))
            Assert.Equal(150, x.Precision);
        // 版 4 は旧番号（0 = 8bit ガンマ）
        using (var x = XcfFile.Open(WriteXcf("p4.xcf", 1, 1, [layer], version: 4, precision: 0)))
            Assert.Equal(150, x.Precision);
        // 版 5・7 の 8bit ガンマ／知覚的
        using (var x = XcfFile.Open(WriteXcf("p5.xcf", 1, 1, [layer], version: 5, precision: 150)))
            Assert.Equal(150, x.Precision);
        using (var x = XcfFile.Open(WriteXcf("p7.xcf", 1, 1, [layer], version: 7, precision: 175)))
            Assert.Equal(175, x.Precision);
        // 8bit リニアは開けるが、解釈が違うことを警告する
        using (var x = XcfFile.Open(WriteXcf("plin.xcf", 1, 1, [layer], version: 11, precision: 100)))
        {
            Assert.Equal(100, x.Precision);
            Assert.Contains(x.Warnings, w => w.Contains("リニア"));
        }
        // 16bit は理由つきで断る
        var deep = WriteXcf("pdeep.xcf", 1, 1, [layer], version: 11, precision: 250);
        var ex = Assert.Throws<NotSupportedException>(() => XcfFile.Open(deep));
        Assert.Contains("16bit", ex.Message);
    }

    [Fact]
    public void EmptyFolderKeepsMarkersPaired()
    {
        var group = new TestXcfLayer { Name = "空", IsGroup = true, ItemPath = [0] };
        var under = TestXcfLayer.Solid("下", 1, 1, 1, 1, 1);
        under.ItemPath = [1];
        var p = WriteXcf("empty.xcf", 1, 1, [group, under], version: 11);
        using var xcf = XcfFile.Open(p);
        Assert.Equal(
            [PsdLayerKind.Pixel, PsdLayerKind.FolderEnd, PsdLayerKind.FolderOpen],
            xcf.Layers.Select(l => l.Kind).ToArray());
        // 空フォルダの後ろのレイヤーが子として飲み込まれていないこと
        Assert.Equal("下", xcf.Layers[0].Path);
        Assert.Null(xcf.Layers[0].Parent);
        Assert.Equal("空", xcf.Layers[2].Path);
    }

    [Fact]
    public void NestedFoldersBuildPaths()
    {
        var g1 = new TestXcfLayer { Name = "外", IsGroup = true, ItemPath = [0] };
        var g2 = new TestXcfLayer { Name = "内", IsGroup = true, ItemPath = [0, 0] };
        var leaf = TestXcfLayer.Solid("葉", 1, 1, 2, 2, 2);
        leaf.ItemPath = [0, 0, 0];
        var root = TestXcfLayer.Solid("根", 1, 1, 3, 3, 3);
        root.ItemPath = [1];
        var p = WriteXcf("nested.xcf", 1, 1, [g1, g2, leaf, root], version: 11);
        using var xcf = XcfFile.Open(p);
        Assert.Equal("外/内/葉", xcf.Find("外/内/葉")?.Path);
        Assert.Equal("外/内", xcf.Find("外/内/葉")?.Parent?.Path);
        Assert.Equal("根", xcf.Layers[0].Path);
    }

    [Fact]
    public void FolderWithOnlyFloatingChildBecomesEmpty()
    {
        var group = new TestXcfLayer { Name = "束", IsGroup = true, ItemPath = [0] };
        var floating = TestXcfLayer.Solid("浮動", 1, 1, 9, 9, 9);
        floating.ItemPath = [0, 0];
        floating.IsFloating = true;
        var keep = TestXcfLayer.Solid("残る", 1, 1, 4, 4, 4);
        keep.ItemPath = [1];
        var p = WriteXcf("floatgrp.xcf", 1, 1, [group, floating, keep], version: 11);
        using var xcf = XcfFile.Open(p);
        Assert.Contains(xcf.Warnings, w => w.Contains("浮動選択"));
        // 浮動選択が抜けた結果として空になったフォルダでも、開始・終了が対になっていること
        Assert.Equal(
            [PsdLayerKind.Pixel, PsdLayerKind.FolderEnd, PsdLayerKind.FolderOpen],
            xcf.Layers.Select(l => l.Kind).ToArray());
        Assert.Equal("残る", xcf.Layers[0].Path);
    }

    [Fact]
    public void EffectsAreDetectedInVersion20()
    {
        var withFx = TestXcfLayer.Solid("効果つき", 1, 1, 1, 2, 3);
        withFx.Effects = 2;
        var plain = TestXcfLayer.Solid("素", 1, 1, 4, 5, 6);
        var p = WriteXcf("fx.xcf", 1, 1, [withFx, plain], version: 20);
        using var xcf = XcfFile.Open(p);
        Assert.True(xcf.LayerInfo[xcf.Find("効果つき")!].HasEffects);
        Assert.False(xcf.LayerInfo[xcf.Find("素")!].HasEffects);
        // 効果つきでも画素は読める（後続レイヤーの読みも壊れていない）
        Assert.NotNull(xcf.GetPixels(xcf.Find("素")!));
    }

    [Fact]
    public void CompositeAndBlendSpacesAreRead()
    {
        var layer = TestXcfLayer.Solid("空間", 1, 1, 1, 1, 1);
        layer.BlendSpace = 2;
        layer.CompositeSpace = 1;
        var plain = TestXcfLayer.Solid("自動", 1, 1, 2, 2, 2);
        var p = WriteXcf("space.xcf", 1, 1, [layer, plain], version: 11);
        using var xcf = XcfFile.Open(p);
        var info = xcf.LayerInfo[xcf.Find("空間")!];
        Assert.Equal(2, info.BlendSpace);
        Assert.Equal(1, info.CompositeSpace);
        Assert.True(info.SpaceNonDefault);
        Assert.False(xcf.LayerInfo[xcf.Find("自動")!].SpaceNonDefault);
    }

    [Fact]
    public void LegacyOverlayMapsToSoftLight()
    {
        Assert.Equal("sLit", XcfFile.MapBlendMode(5, out _, out _));  // レガシー オーバーレイ
        Assert.Equal("over", XcfFile.MapBlendMode(23, out _, out _)); // 標準 オーバーレイ
        XcfFile.MapBlendMode(1, out bool dissApprox, out _);          // ディザ合成は近似
        Assert.True(dissApprox);
    }

    [Fact]
    public void CorruptTileDataThrows()
    {
        // RLE の長さ 0 命令
        var bad = TestXcfLayer.Solid("壊", 2, 2, 1, 1, 1);
        bad.TileOverride = [127, 0, 0, 65];
        var p = WriteXcf("badrle.xcf", 2, 2, [bad]);
        using var xcf = XcfFile.Open(p);
        Assert.Throws<InvalidDataException>(() => xcf.GetPixels(xcf.PixelLayers.Single()));

        // レイヤーの種類と bpp の不一致
        var mismatch = TestXcfLayer.Solid("不一致", 2, 2, 1, 1, 1);
        mismatch.BppOverride = 3;
        var p2 = WriteXcf("badbpp.xcf", 2, 2, [mismatch]);
        using var xcf2 = XcfFile.Open(p2);
        Assert.Throws<InvalidDataException>(() => xcf2.GetPixels(xcf2.PixelLayers.Single()));
    }

    [Fact]
    public void ManyLayersAndHugeCanvas()
    {
        var layers = new List<TestXcfLayer>();
        for (int i = 0; i < 300; i++) layers.Add(TestXcfLayer.Solid($"層{i}", 1, 1, 1, 1, 1));
        var p = WriteXcf("many.xcf", 1, 1, layers, version: 11);
        using var xcf = XcfFile.Open(p);
        Assert.Equal(300, xcf.PixelLayers.Count());
        Assert.NotNull(xcf.Find("層299"));

        var huge = WriteXcf("huge.xcf", 30001, 1, [TestXcfLayer.Solid("x", 1, 1, 0, 0, 0)]);
        Assert.Throws<InvalidDataException>(() => XcfFile.Open(huge));
    }

    [Fact]
    public void CacheEvictsOldestLayers()
    {
        var top = TestXcfLayer.Solid("上", 64, 64, 8, 8, 8);
        var bottom = TestXcfLayer.Solid("下", 64, 64, 9, 9, 9);
        var p = WriteXcf("cache.xcf", 64, 64, [top, bottom]);
        using var xcf = XcfFile.Open(p);
        xcf.CacheLimitBytes = 64 * 64 * 4; // 1 枚ぶんだけ
        var pxB = xcf.GetPixels(xcf.Find("下")!);
        Assert.Equal(64 * 64 * 4, xcf.CacheBytes);
        var pxT = xcf.GetPixels(xcf.Find("上")!);
        Assert.Equal(64 * 64 * 4, xcf.CacheBytes); // 古い方が捨てられ、上限に収まる
        // 捨てられたレイヤーも、もう一度読めば正しく返る
        var pxB2 = xcf.GetPixels(xcf.Find("下")!);
        Assert.Equal(pxB!.Bgra, pxB2!.Bgra);
        Assert.NotNull(pxT);
    }

    [Fact]
    public void MaskAndEffectsCombinations()
    {
        // 版 20: マスクの読みでファイル位置がずれると、効果ポインタ列を変な場所から読んでしまう。
        // 4 通りの組み合わせで、効果の有無とマスク適用がそれぞれ正しいことを確かめる
        var both = TestXcfLayer.Solid("両方", 2, 2, 200, 100, 50, 255);
        both.Mask = [128, 128, 128, 128];
        both.Effects = 2;
        var maskOnly = TestXcfLayer.Solid("マスクのみ", 2, 2, 200, 100, 50, 255);
        maskOnly.Mask = [128, 128, 128, 128];
        var fxOnly = TestXcfLayer.Solid("効果のみ", 2, 2, 200, 100, 50, 255);
        fxOnly.Effects = 1;
        var neither = TestXcfLayer.Solid("なし", 2, 2, 200, 100, 50, 255);
        var p = WriteXcf("maskfx.xcf", 2, 2, [both, maskOnly, fxOnly, neither], version: 20);
        using var xcf = XcfFile.Open(p);
        Assert.True(xcf.LayerInfo[xcf.Find("両方")!].HasEffects);
        Assert.False(xcf.LayerInfo[xcf.Find("マスクのみ")!].HasEffects);
        Assert.True(xcf.LayerInfo[xcf.Find("効果のみ")!].HasEffects);
        Assert.False(xcf.LayerInfo[xcf.Find("なし")!].HasEffects);
        // マスクは効果つきでも効く（α が 128 になる）
        var px = xcf.GetPixels(xcf.Find("両方")!);
        Assert.Equal(128, px!.Bgra[3]);
        var pxFx = xcf.GetPixels(xcf.Find("効果のみ")!);
        Assert.Equal(255, pxFx!.Bgra[3]);
    }

    [Fact]
    public void ParasitesDetectIccProfile()
    {
        var layer = TestXcfLayer.Solid("x", 1, 1, 1, 1, 1);
        using (var x = XcfFile.Open(WriteXcf("icc.xcf", 1, 1, [layer], version: 11,
            parasites: [("icc-profile", new byte[] { 1, 2, 3 })])))
        {
            Assert.True(x.HasIccProfile);
            Assert.Contains(x.Warnings, w => w.Contains("ICC"));
        }
        using (var x = XcfFile.Open(WriteXcf("noicc.xcf", 1, 1, [layer], version: 11,
            parasites: [("gimp-comment", new byte[] { 65 })])))
            Assert.False(x.HasIccProfile);
        using (var x = XcfFile.Open(WriteXcf("multi.xcf", 1, 1, [layer], version: 11,
            parasites: [("gimp-comment", new byte[] { 65 }), ("icc-profile", new byte[] { 9 })])))
            Assert.True(x.HasIccProfile);
        // 大きさが壊れた parasite があっても、開くこと自体は失敗しない
        var raw = new List<byte>();
        void U32(uint v) { raw.Add((byte)(v >> 24)); raw.Add((byte)(v >> 16)); raw.Add((byte)(v >> 8)); raw.Add((byte)v); }
        var nameBytes = System.Text.Encoding.UTF8.GetBytes("icc-profile\0");
        U32((uint)nameBytes.Length);
        raw.AddRange(nameBytes);
        U32(1);
        U32(9999); // 実データより大きい
        raw.AddRange(new byte[] { 1, 2 });
        using var x2 = XcfFile.Open(WriteXcf("badpara.xcf", 1, 1, [layer], version: 11, parasitesRaw: raw.ToArray()));
        Assert.NotNull(x2);
    }

    [Fact]
    public void NegativeSpaceMeansAuto()
    {
        var auto = TestXcfLayer.Solid("自動", 1, 1, 1, 1, 1);
        auto.BlendSpace = -3; // 負数 = 自動（絶対値は保存時点の解決値）
        var expl = TestXcfLayer.Solid("明示", 1, 1, 2, 2, 2);
        expl.CompositeSpace = 2;
        var p = WriteXcf("negspace.xcf", 1, 1, [auto, expl], version: 11);
        using var xcf = XcfFile.Open(p);
        Assert.False(xcf.LayerInfo[xcf.Find("自動")!].SpaceNonDefault);
        Assert.True(xcf.LayerInfo[xcf.Find("明示")!].SpaceNonDefault);
    }

    [Fact]
    public void Version24IsRejected()
    {
        var p = WriteXcf("v24.xcf", 1, 1, [TestXcfLayer.Solid("x", 1, 1, 0, 0, 0)], version: 24);
        var ex = Assert.Throws<NotSupportedException>(() => XcfFile.Open(p));
        Assert.Contains("24", ex.Message);
    }

    [Fact]
    public void LayerTypeMustMatchImageType()
    {
        var gray = new TestXcfLayer { Name = "灰", Type = 2, Width = 1, Height = 1, Pixels = [7] };
        var p = WriteXcf("typemix.xcf", 1, 1, [gray], baseType: 0); // RGB 画像にグレーのレイヤー
        Assert.Throws<InvalidDataException>(() => XcfFile.Open(p));
    }

    [Fact]
    public void OversizeItemIsNotCached()
    {
        var layer = TestXcfLayer.Solid("大", 64, 64, 5, 5, 5);
        var p = WriteXcf("big1.xcf", 64, 64, [layer]);
        using var xcf = XcfFile.Open(p);
        xcf.CacheLimitBytes = 100; // 1 枚にも満たない上限
        var px = xcf.GetPixels(xcf.PixelLayers.Single());
        Assert.NotNull(px);
        Assert.Equal(0, xcf.CacheBytes); // 上限を超える 1 枚は残さない
        var px2 = xcf.GetPixels(xcf.PixelLayers.Single());
        Assert.Equal(px!.Bgra, px2!.Bgra);
    }

    [Fact]
    public void BadUserUnitLengthIsTolerated()
    {
        // 昔の GIMP は PROP_USER_UNIT の長さを実際より短く書いた（実ファイルで確認）。
        // 長さを鵜呑みにすると後続のプロパティが全部ずれるので、中身の形で読み進める。
        // USER_UNIT の後ろにある Parasite（ICC）まで正しく届くことで、ずれていないことを確かめる
        var layer = TestXcfLayer.Solid("x", 1, 1, 3, 4, 5);
        var p = WriteXcf("userunit.xcf", 1, 1, [layer], version: 0, badUserUnit: true,
            parasites: [("icc-profile", new byte[] { 1 })]);
        using var xcf = XcfFile.Open(p);
        Assert.True(xcf.HasIccProfile);
        Assert.NotNull(xcf.GetPixels(xcf.PixelLayers.Single()));
    }

    [Fact]
    public void TextLayerDetectedByParasite()
    {
        // GIMP 3 の文字レイヤーは PROP_TEXT_LAYER_FLAGS ではなく
        // レイヤーの Parasite「gimp-text-layer」で見分ける（実ファイルで確認）
        var t = TestXcfLayer.Solid("文字", 1, 1, 1, 2, 3);
        t.Parasites = [("gimp-text-layer", new byte[] { 1 })];
        var p = WriteXcf("textpara.xcf", 1, 1, [t], version: 19);
        using var xcf = XcfFile.Open(p);
        Assert.Equal(PsdLayerKind.Text, xcf.Layers.Single().Kind);
    }
}
