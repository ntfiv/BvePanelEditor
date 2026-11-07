using BvePanelEditor.Core;
using BvePanelEditor.Core.Crop;
using BvePanelEditor.Core.Psd;
using Xunit;

namespace BvePanelEditor.Tests;

/// <summary>画像からパネルを作る: パネル本体の下に敷く・上に重ねる画像。</summary>
public sealed class CropOverlayTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "panelkun_overlay_" + Guid.NewGuid().ToString("N"));
    public CropOverlayTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static PsdPixels Solid(int w, int h, byte v) => new(new PsdRect(0, 0, w, h), Enumerable.Repeat(v, w * h * 4).ToArray());

    private CropBuildSpec Spec(params CropOverlay[] overlays)
    {
        var lit = Solid(100, 60, 200);
        return new CropBuildSpec
        {
            PanelDir = _dir, OutputDir = Path.Combine(_dir, "crop"),
            UnlitDayPath = Path.Combine(_dir, "panel.png"), UnlitNightPath = Path.Combine(_dir, "panel_N.png"),
            Width = 100, Height = 60,
            Sets = [new CropSetImages { DayPath = Path.Combine(_dir, "panel_TT.png"), Day = lit }],
            Regions = [new CropRegion { Rect = new PsdRect(10, 10, 20, 20), Name = "灯", Subject = "ats0", Layer = 1 }],
            Overlays = [.. overlays],
        };
    }

    private CropOverlay O(string name, bool below, int x = 0, int y = 0, int? layer = null, string subject = "true") =>
        new() { Name = name, Below = below, DayPath = Path.Combine(_dir, name + ".png"), X = x, Y = y, Layer = layer, Subject = subject };

    /// <summary>[This] の画像は作らない（透明な画像は書き出さない）。</summary>
    private static void AssertBlankThis(CropBuildResult r)
    {
        Assert.DoesNotContain(r.Files, f => Path.GetFileName(f.FullPath).StartsWith("this_blank_"));
    }

    [Fact]
    public void WithoutOverlaysMainPanelIsALamp()
    {
        var r = CropBuild.Build(Spec());
        AssertBlankThis(r);
        var main = r.Sections[0];
        Assert.Same(r.Main, main);
        Assert.True(main.IsMainPanel);
        Assert.Equal((MainPanel.Title, "true", "0, 0", "1", "panel.png", "panel_N.png"),
            (main.Title, main.Get("Subject"), main.Get("Location"), main.Get("Layer"), main.Get("DaytimeImage"), main.Get("NighttimeImage")));
        Assert.Equal("2", r.Sections.Single(s => s.Title == "灯").Get("Layer")); // メインパネルより上
        Assert.DoesNotContain(r.Warnings, w => w.Contains("レイヤー")); // メインパネルの上に置くのはいつものことなので知らせない
    }

    [Fact]
    public void UnderlaysAndOverlaysLikeCommonPanels()
    {
        // bg.png はいちばん下（レイヤー 1）、gs.png は 1400,0 のレイヤー 2、メインパネルをその上、カバーを表示灯より上に
        var bg = O("bg", true);
        bg.NightPath = Path.Combine(_dir, "bgN.png");
        var r = CropBuild.Build(Spec(bg, O("gs", true, 1400, 0), O("カバー", false, 5, 6)));
        AssertBlankThis(r);
        Assert.Empty(r.Warnings.Where(w => w.Contains("位置")));

        var under = r.Sections.Single(s => s.Title == "bg");
        Assert.Equal(("true", "0, 0", "1", "bg.png", "bgN.png"), (under.Get("Subject"), under.Get("Location"), under.Get("Layer"), under.Get("DaytimeImage"), under.Get("NighttimeImage")));
        var gs = r.Sections.Single(s => s.Title == "gs");
        Assert.Equal(("true", "1400, 0", "2", "gs.png"), (gs.Get("Subject"), gs.Get("Location"), gs.Get("Layer"), gs.Get("DaytimeImage")));
        var body = r.Sections.Single(s => s.IsMainPanel);
        Assert.Equal((MainPanel.Title, "true", "0, 0", "3", "panel.png", "panel_N.png"), (body.Title, body.Get("Subject"), body.Get("Location"), body.Get("Layer"), body.Get("DaytimeImage"), body.Get("NighttimeImage")));
        Assert.Null(body.CropSet); // 元の画像を指すだけなので、切り抜き元から更新では触らない
        var lamp = r.Sections.Single(s => s.Title == "灯");
        Assert.Equal("4", lamp.Get("Layer"));                 // メインパネルより上
        var cover = r.Sections.Single(s => s.Title == "カバー");
        Assert.Equal(("5, 6", "5"), (cover.Get("Location"), cover.Get("Layer")));
        // 重なりの順（パネルファイルの書き順）: 下に敷くもの → メインパネル → 表示灯 → 上に重ねるもの
        Assert.Equal(["bg", "gs", MainPanel.Title, "灯", "カバー"], r.Sections.Select(s => s.Title));
    }

    [Fact]
    public void ExplicitLayersAndSubject()
    {
        var r = CropBuild.Build(Spec(O("bg", true), O("ガラス", true, layer: 4), O("扉", false, layer: 20, subject: "door")));
        Assert.Equal("1", r.Sections.Single(s => s.Title == "bg").Get("Layer"));
        Assert.Equal("4", r.Sections.Single(s => s.Title == "ガラス").Get("Layer"));
        Assert.Equal("5", r.Sections.Single(s => s.IsMainPanel).Get("Layer"));
        var door = r.Sections.Single(s => s.Title == "扉");
        Assert.Equal(("20", "door"), (door.Get("Layer"), door.Get("Subject")));
    }

    [Fact]
    public void BottomUnderlayKeepsItsPosition()
    {
        // いちばん下に敷く画像も表示灯なので、位置はそのまま使える
        var r = CropBuild.Build(Spec(O("bg", true, 30, 40)));
        AssertBlankThis(r);
        Assert.Equal("30, 40", r.Sections.Single(s => s.Title == "bg").Get("Location"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("0, 0 にしました"));
    }

    [Fact]
    public void PsdOverlaysAreWrittenAndLinked()
    {
        var px = Solid(100, 60, 120);
        var bg = O("bg", true);
        bg.DayPath = Path.Combine(_dir, "bg.psd");
        bg.DayLayers = ["背景"];
        bg.Day = px;
        var cover = O("カバー", false);
        cover.DayPath = Path.Combine(_dir, "cover.psd");
        cover.DayLayers = ["カバー"];
        cover.Day = px;
        var moved = O("ずらす", false, 10, 0);
        moved.DayPath = Path.Combine(_dir, "cover.psd");
        moved.DayLayers = ["カバー"];
        moved.Day = px;
        var r = CropBuild.Build(Spec(bg, cover, moved));
        AssertBlankThis(r);
        var under = r.Sections.Single(s => s.Title == "bg");
        Assert.Equal(Path.Combine("crop", "bg.png"), under.Get("DaytimeImage"));    // PSD から重ねた絵を書き出して表示灯に
        Assert.Equal("重ね bg", under.CropSet);
        Assert.DoesNotContain(r.Sets, c => c.Name == CropSetNames.Base);          // [This] は透明なのでつながりなし
        var c = r.Sections.Single(s => s.Title == "カバー");
        Assert.Equal("重ね カバー", c.CropSet);
        Assert.Contains(r.Sets, s => s.Name == "重ね カバー");
        Assert.Null(r.Sections.Single(s => s.Title == "ずらす").CropSet);       // 位置が 0, 0 でないものはつながりなし
        Assert.All(r.Files, f => Assert.True(PngWriter.IsPanelKunPngBytes(f.Png)));
    }

    [Fact]
    public void PsdBodyIsLinkedAsMainPanel()
    {
        // 4 点のパネル本体が PSD のとき、そのつながりは「パネル本体」で、メインパネルの表示灯が使う
        var spec = Spec(O("bg", true));
        spec.UnlitDayLayers = ["板"];
        spec.UnlitDay = Solid(100, 60, 50);
        var r = CropBuild.Build(spec);
        Assert.Contains(r.Sets, c => c.Name == CropSetNames.PanelBody);
        Assert.DoesNotContain(r.Sets, c => c.Name == CropSetNames.Base);
        Assert.Equal(CropSetNames.PanelBody, r.Sections.Single(s => s.IsMainPanel).CropSet);
    }
}
