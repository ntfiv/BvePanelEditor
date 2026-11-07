using BvePanelEditor.Core;
using BvePanelEditor.Core.Crop;
using BvePanelEditor.Core.Psd;
using Xunit;

namespace BvePanelEditor.Tests;

/// <summary>書き出す画像を 2 の累乗の大きさにする。</summary>
public sealed class PngPow2Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "panelkun_pow2_" + Guid.NewGuid().ToString("N"));
    public PngPow2Tests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 4)]
    [InlineData(64, 64)]
    [InlineData(65, 128)]
    [InlineData(2000, 2048)]
    public void NextPow2(int n, int expected) => Assert.Equal(expected, PngWriter.NextPow2(n));

    [Fact]
    public void PaddedPngKeepsContentSize()
    {
        var px = new PsdPixels(new PsdRect(5, 5, 8, 10), Enumerable.Repeat((byte)200, 3 * 5 * 4).ToArray());
        var p = Path.Combine(_dir, "a.png");
        File.WriteAllBytes(p, PngWriter.EncodePow2(px));
        Assert.True(PngWriter.TryReadSize(p, out var w, out var h));
        Assert.Equal((4, 8), (w, h));
        Assert.True(PngWriter.TryReadContentSize(p, out w, out h));
        Assert.Equal((3, 5), (w, h));
        Assert.True(PngWriter.IsPanelKunPng(p));

        // 印だけの古い PNG は、PNG の大きさが中身の大きさ
        var old = Path.Combine(_dir, "old.png");
        File.WriteAllBytes(old, PngWriter.Encode(px));
        Assert.True(PngWriter.TryReadContentSize(old, out w, out h));
        Assert.Equal((3, 5), (w, h));
    }

    [Fact]
    public void UpdateUpgradesOldUnpaddedImages()
    {
        // 以前の版で書き出した 10×6 の表示灯（2 の累乗でない）を更新すると、16×8 に広げて書き直す
        var src = new PsdPixels(new PsdRect(0, 0, 64, 32), Enumerable.Repeat((byte)255, 64 * 32 * 4).ToArray());
        var srcPath = Path.Combine(_dir, "lit.png");
        Directory.CreateDirectory(Path.Combine(_dir, "crop"));
        var lamp = Path.Combine(_dir, "crop", "灯.png");
        File.WriteAllBytes(lamp, PngWriter.Encode(RegionDetect.CropOf(src, new PsdRect(10, 10, 20, 16))));

        var doc = PanelDocument.CreateNew("base.png", 64, 32);
        doc.CropSets.Add(new CropSet { Name = "発光", Day = "lit.png" });
        var sec = PanelSection.Create(SectionKind.PilotLamp);
        sec.Set("Location", "10, 10");
        sec.Set("DaytimeImage", Path.Combine("crop", "灯.png"));
        sec.CropSet = "発光";
        doc.Sections.Add(sec);

        var plan = CropUpdate.Plan(doc, _dir, p => Path.GetFullPath(p) == Path.GetFullPath(srcPath) ? src : null);
        var f = Assert.Single(plan.Files);
        var png = TestPngReader.Read(f.Png);
        Assert.Equal((16, 8), (png.W, png.H));
        Assert.Equal(255, png.Rgba[(5 * 16 + 9) * 4 + 3]);
        Assert.Equal(0, png.Rgba[(0 * 16 + 10) * 4 + 3]);
    }

    [Fact]
    public void WarnsWhenBaseImageIsNotPow2()
    {
        var px = new PsdPixels(new PsdRect(0, 0, 100, 60), new byte[100 * 60 * 4]);
        var spec = new CropBuildSpec
        {
            PanelDir = _dir, OutputDir = Path.Combine(_dir, "crop"), UnlitDayPath = Path.Combine(_dir, "base.png"), Width = 100, Height = 60,
        };
        Assert.Contains(CropBuild.Build(spec).Warnings, w => w.Contains("2 の累乗"));

        // PSD から重ねて書き出す本体は広げるので、知らせない
        spec.UnlitDayLayers = ["板"];
        spec.UnlitDay = px;
        var r = CropBuild.Build(spec);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("2 の累乗"));
        var basePng = TestPngReader.Read(r.Files[0].Png);
        Assert.Equal((128, 64), (basePng.W, basePng.H));
    }
}
