using BvePanelEditor.Core.Psd;
using BvePanelEditor.Core.Xcf;
using Xunit;

namespace BvePanelEditor.Tests;

/// <summary>PR-B: XCF が PSD と同じ読み込みの仕組み（ILayerImage）で使えること。昼 XCF＋夜 PSD の混在も含む。</summary>
public sealed class XcfImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "panelkun_xcfimp_" + Guid.NewGuid().ToString("N"));

    public XcfImportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string WriteXcf(string name, int w, int h, IReadOnlyList<TestXcfLayer> layersTopFirst,
        int version = 11, IReadOnlyList<(string Name, byte[] Data)>? parasites = null)
    {
        var p = Path.Combine(_dir, name);
        TestXcfWriter.Write(p, w, h, layersTopFirst, version, parasites: parasites);
        return p;
    }

    private string WritePsd(string name, int w, int h, IReadOnlyList<TestLayer> layersBottomFirst)
    {
        var p = Path.Combine(_dir, name);
        TestPsdWriter.Write(p, w, h, layersBottomFirst);
        return p;
    }

    private static (byte R, byte G, byte B, byte A) At(PsdPixels px, int cx, int cy)
    {
        int i = ((cy - px.Bounds.Top) * px.Width + (cx - px.Bounds.Left)) * 4;
        return (px.Bgra[i + 2], px.Bgra[i + 1], px.Bgra[i], px.Bgra[i + 3]);
    }

    [Fact]
    public void OpenDispatchesByExtension()
    {
        var xcfPath = WriteXcf("a.xcf", 2, 2, [TestXcfLayer.Solid("板", 2, 2, 1, 2, 3)]);
        var psdPath = WritePsd("a.psd", 2, 2, [TestLayer.Solid("板", new PsdRect(0, 0, 2, 2), 1, 2, 3)]);
        using var a = LayerImage.Open(xcfPath);
        using var b = LayerImage.Open(psdPath);
        Assert.IsType<XcfFile>(a);
        Assert.IsType<PsdFile>(b);
        Assert.True(LayerImage.IsSupported("x.XCF"));
        Assert.True(LayerImage.IsSupported("x.psd"));
        Assert.False(LayerImage.IsSupported("x.png"));
    }

    [Fact]
    public void ComposeBaseReadsXcf()
    {
        var top = TestXcfLayer.Solid("上", 2, 2, 0, 0, 200);
        top.OffsetX = 1;
        var p = WriteXcf("base.xcf", 4, 2, [top, TestXcfLayer.Solid("下", 4, 2, 200, 0, 0)]);
        using var xcf = XcfFile.Open(p);
        var warnings = new List<PsdWarning>();
        var img = PsdImport.ComposeBase(xcf, ["下", "上"], warnings, "昼");
        Assert.NotNull(img);
        Assert.Equal((byte)200, At(img!, 0, 0).R);
        Assert.Equal((byte)200, At(img!, 1, 0).B);
        Assert.Empty(warnings);
    }

    [Fact]
    public void LampMixesXcfDayAndPsdNight()
    {
        var xcfLamp = TestXcfLayer.Solid("戸", 2, 2, 0, 180, 0);
        xcfLamp.OffsetX = 1; xcfLamp.OffsetY = 1;
        var dayPath = WriteXcf("day.xcf", 8, 4, [xcfLamp]);
        var nightPath = WritePsd("night.psd", 8, 4, [TestLayer.Solid("戸", new PsdRect(1, 1, 3, 3), 220, 200, 0)]);
        using var day = LayerImage.Open(dayPath);
        using var night = LayerImage.Open(nightPath);
        var warnings = new List<PsdWarning>();
        var lamp = PsdImport.RenderLamp(day, night, "戸", warnings);
        Assert.NotNull(lamp);
        Assert.Equal(new PsdRect(1, 1, 3, 3), lamp!.Rect);
        Assert.Equal((byte)180, At(lamp.Day, 1, 1).G);
        Assert.NotNull(lamp.Night);
        Assert.Equal((byte)220, At(lamp.Night!, 1, 1).R);

        // 逆の組み合わせ（昼 PSD・夜 XCF）も同じようにいく
        var warnings2 = new List<PsdWarning>();
        var lamp2 = PsdImport.RenderLamp(night, day, "戸", warnings2);
        Assert.NotNull(lamp2);
        Assert.Equal((byte)220, At(lamp2!.Day, 1, 1).R);
        Assert.Equal((byte)180, At(lamp2.Night!, 1, 1).G);
    }

    [Fact]
    public void BuildFromXcfEmitsBaseLampAndFileWarnings()
    {
        var lamp = TestXcfLayer.Solid("戸", 2, 2, 0, 180, 0);
        lamp.OffsetX = 1; lamp.OffsetY = 1;
        var p = WriteXcf("panel.xcf", 8, 4, [lamp, TestXcfLayer.Solid("背景", 8, 4, 30, 30, 30)],
            parasites: [("icc-profile", new byte[] { 1, 2, 3 })]);
        using var day = XcfFile.Open(p);
        var r = PsdImport.Build(new PsdImportSpec
        {
            Day = day, PanelDir = _dir, OutputDir = Path.Combine(_dir, "out"),
            BaseLayers = ["背景"], LampLayers = ["戸"],
        });
        Assert.NotNull(r.Base);
        Assert.Contains(r.Files, f => f.FullPath.EndsWith("base.png"));
        Assert.Contains(r.Sections, s => s.Title == "戸");
        Assert.Contains(r.Warnings, w => w.Kind == PsdWarningKind.Approx
            && w.Text == "昼: ICC プロファイルは反映されません（色の数値をそのまま読みます）");
    }

    [Fact]
    public void NoIccProfileMeansNoIccWarning()
    {
        // ICC の無い普通の XCF では、ICC 警告（文字列・種類つきの両方）が出ないこと
        var p = WriteXcf("plain.xcf", 4, 2, [TestXcfLayer.Solid("背景", 4, 2, 30, 30, 30)]);
        using var day = XcfFile.Open(p);
        Assert.False(day.HasIccProfile);
        Assert.DoesNotContain(day.Warnings, w => w.Contains("ICC"));
        Assert.Empty(day.FileWarnings);
        var r = PsdImport.Build(new PsdImportSpec
        {
            Day = day, PanelDir = _dir, OutputDir = Path.Combine(_dir, "out"), BaseLayers = ["背景"],
        });
        Assert.DoesNotContain(r.Warnings, w => w.Text.Contains("ICC"));
    }

    [Fact]
    public void XcfLayerCautionsAreClassified()
    {
        var grain = TestXcfLayer.Solid("粒", 2, 2, 9, 9, 9);
        grain.Mode = 47; // グレイン統合（非対応）
        var hue = TestXcfLayer.Solid("色相", 2, 2, 9, 9, 9);
        hue.Mode = 24; // 色相（LCh）（近似）
        var fx = TestXcfLayer.Solid("効果", 2, 2, 9, 9, 9);
        fx.Effects = 1;
        var diss = TestXcfLayer.Solid("ディザ", 2, 2, 9, 9, 9);
        diss.Mode = 1; // ディザ合成（近似。注意は PSD 側の決まりで付く）
        var p = WriteXcf("cautions.xcf", 2, 2, [grain, hue, fx, diss], version: 20);
        using var xcf = XcfFile.Open(p);

        var g = PsdImport.CautionItems(xcf.Find("粒")!);
        Assert.Contains(g, w => w.Kind == PsdWarningKind.Unsupported && w.Text.Contains("描画モード（グレイン統合）"));
        Assert.Equal("×非対応", PsdImport.CautionTags(g));

        var hu = PsdImport.CautionItems(xcf.Find("色相")!);
        Assert.Contains(hu, w => w.Kind == PsdWarningKind.Approx && w.Text.Contains("描画モード（色相（LCh））は近似です"));
        Assert.Equal("△近似", PsdImport.CautionTags(hu));

        var f = PsdImport.CautionItems(xcf.Find("効果")!);
        Assert.Contains(f, w => w.Kind == PsdWarningKind.Unsupported && w.Text.Contains("レイヤー効果"));

        var d = PsdImport.CautionItems(xcf.Find("ディザ")!);
        Assert.Contains(d, w => w.Kind == PsdWarningKind.Approx && w.Text.Contains("ディザ合成は通常として重ねます"));
        Assert.DoesNotContain(d, w => w.Text.Contains("モード 1"));
    }

    [Fact]
    public void LinearCompositeMatchesGimp()
    {
        // 白の上に黒 50%: GIMP（版 11 以降）はリニアで混ぜるので、sRGB の単純平均 127 ではなく 188 になる
        byte GrayAt(string name, int version)
        {
            var top = TestXcfLayer.Solid("黒", 2, 2, 0, 0, 0, 128); top.Mode = 28; // 通常（今の方式）
            var bottom = TestXcfLayer.Solid("白", 2, 2, 255, 255, 255); bottom.Mode = 28;
            var p = WriteXcf(name, 2, 2, [top, bottom], version: version);
            using var xcf = XcfFile.Open(p);
            Assert.Equal(version >= 11, xcf.LinearComposite);
            var px = PsdCompose.Compose(xcf, xcf.ComposableLayers.ToList(), xcf.Canvas);
            return px.Bgra[2]; // R
        }
        Assert.InRange(GrayAt("lin11.xcf", 11), (byte)184, (byte)192);
        Assert.InRange(GrayAt("lin3.xcf", 3), (byte)124, (byte)131);
    }

    [Fact]
    public void BlendModesUseTheirGimpSpace()
    {
        // GIMP は描画モードごとに計算する色空間が違う（実ファイル 03 の書き出しで照合した分類）
        byte ComposeGray(string name, int mode, byte bottom, byte top)
        {
            var t = TestXcfLayer.Solid("上", 2, 2, top, top, top);
            t.Mode = mode;
            var p = WriteXcf(name, 2, 2, [t, TestXcfLayer.Solid("下", 2, 2, bottom, bottom, bottom)], version: 11);
            using var xcf = XcfFile.Open(p);
            var px = PsdCompose.Compose(xcf, xcf.ComposableLayers.ToList(), xcf.Canvas);
            return px.Bgra[2]; // R
        }
        // スクリーン（31）は sRGB で計算: 64 の上に 192 → sRGB の 208（リニアで混ぜると 197 になる）
        Assert.InRange(ComposeGray("scr.xcf", 31, 64, 192), (byte)205, (byte)211);
        // 加算（33）はリニアで計算: 100 + 100 → sRGB の 200 ではなく 139
        Assert.InRange(ComposeGray("add.xcf", 33, 100, 100), (byte)136, (byte)142);
        // 加算の旧方式（7）は sRGB で計算: 100 + 100 → 200
        Assert.InRange(ComposeGray("addleg.xcf", 7, 100, 100), (byte)197, (byte)203);
    }

    [Fact]
    public void ComposeAndBuildEmitEffectCaution()
    {
        // レイヤー効果（非破壊フィルター）の知らせは、行表示だけでなく合成・読み込みの警告にも出す
        var fx = TestXcfLayer.Solid("効果", 2, 2, 9, 9, 9);
        fx.Effects = 1;
        var p = WriteXcf("fxwarn.xcf", 2, 2, [fx], version: 20);
        using var xcf = XcfFile.Open(p);
        var layer = xcf.Find("効果")!;
        var w = new List<PsdWarning>();
        _ = PsdCompose.Compose(xcf, [layer], xcf.Canvas, 1, w);
        Assert.Contains(w, x => x.Kind == PsdWarningKind.Unsupported && x.Text.Contains("レイヤー効果"));
        var r = PsdImport.Build(new PsdImportSpec
        {
            Day = xcf, PanelDir = _dir, OutputDir = Path.Combine(_dir, "out"), BaseLayers = ["効果"],
        });
        Assert.Contains(r.Warnings, x => x.Kind == PsdWarningKind.Unsupported && x.Text.Contains("レイヤー効果"));
    }

    [Fact]
    public void ApplyMaskFalseMarksMaskDisabled()
    {
        var off = TestXcfLayer.Solid("切", 2, 2, 9, 9, 9);
        off.Mask = [128, 128, 128, 128];
        off.ApplyMask = false;
        var on = TestXcfLayer.Solid("入", 2, 2, 9, 9, 9);
        on.Mask = [128, 128, 128, 128];
        var p = WriteXcf("masks.xcf", 2, 2, [off, on]);
        using var xcf = XcfFile.Open(p);
        var lOff = xcf.Find("切")!;
        Assert.True(lOff.MaskDisabled);
        Assert.Null(xcf.GetMask(lOff));
        var lOn = xcf.Find("入")!;
        Assert.False(lOn.MaskDisabled);
        Assert.NotNull(xcf.GetMask(lOn));
    }

    [Fact]
    public void FindAllSameNameWorksForXcf()
    {
        var p = WriteXcf("dup.xcf", 2, 2,
        [
            TestXcfLayer.Solid("コマ", 2, 2, 1, 1, 1),
            TestXcfLayer.Solid("コマ", 2, 2, 2, 2, 2),
            TestXcfLayer.Solid("他", 2, 2, 3, 3, 3),
        ]);
        using var xcf = XcfFile.Open(p);
        Assert.Equal(2, xcf.FindAllSameName("コマ").Count);
        Assert.Single(xcf.FindAllSameName("他"));
        Assert.Empty(xcf.FindAllSameName("無い"));
    }
}
