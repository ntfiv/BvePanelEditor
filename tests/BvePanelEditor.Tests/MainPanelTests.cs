using BvePanelEditor.Core;
using BvePanelEditor.Core.Crop;
using Xunit;

namespace BvePanelEditor.Tests;

/// <summary>メインパネル: [This] は透明、パネルの土台は常表示の表示灯。</summary>
public sealed class MainPanelTests
{
    private const string Panel = "BveTs Instrument Panel 2.00\r\n\r\n[This]\r\nResolution = 1000\r\nDaytimeImage = panel.png\r\nNighttimeImage = panel_N.png\r\nTransparentColor = 000000\r\nCenter = 500, 300\r\n"
        + "\r\n[PilotLamp]\r\nSubject = ats1\r\nLocation = 10, 10\r\nDaytimeImage = a.png\r\nLayer = 1\r\n"
        + "\r\n[Needle]\r\nSubject = kmph\r\nLocation = 100, 100\r\nDaytimeImage = n.png\r\nLayer = 3\r\n";

    private static string Layer(PanelDocument d, int i) => d.Sections[i].Get("Layer") ?? "";

    [Fact]
    public void MarkerRoundTrips()
    {
        var text = "BveTs Instrument Panel 2.00\r\n[This]\r\nDaytimeImage = this_blank_10x10.png\r\n\r\n;メインパネル\r\n[PilotLamp]\r\n;@メインパネル\r\nSubject = true\r\nLocation = 0, 0\r\n";
        var doc = PanelDocument.Parse(text);
        Assert.Empty(doc.GroupWarnings);
        Assert.Same(doc.Sections[1], doc.MainPanel);
        Assert.DoesNotContain(doc.Sections[1].Lines, l => l.Raw.Contains("メインパネル"));
        Assert.Equal(text, doc.Serialize());
        Assert.True(doc.Sections[1].Clone().IsMainPanel);
    }

    [Fact]
    public void MarkerOnlyOnLamps()
    {
        var doc = PanelDocument.Parse("BveTs Instrument Panel 2.00\r\n[Needle]\r\n;@メインパネル\r\nSubject = kmph\r\n");
        Assert.Null(doc.MainPanel);
        Assert.Single(doc.GroupWarnings);
    }

    [Fact]
    public void CreateNewHasThisWithoutImageAndMainLamp()
    {
        var doc = MainPanel.CreateNew("panel.png", "panel_N.png", 2048, 1536);
        Assert.Null(doc.This!.Get("DaytimeImage"));
        Assert.DoesNotContain("DaytimeImage", doc.This.HeaderRaw + string.Join("", doc.This.Lines.Select(l => l.Raw)));
        Assert.True(MainPanel.ThisHasNoImage(doc));
        Assert.Null(doc.This.Get("NighttimeImage"));
        Assert.Equal("2048", doc.This.Get("Resolution"));
        var main = doc.MainPanel!;
        Assert.Equal(("true", "0, 0", "1", "panel.png", "panel_N.png", MainPanel.Title),
            (main.Get("Subject"), main.Get("Location"), main.Get("Layer"), main.Get("DaytimeImage"), main.Get("NighttimeImage"), main.Title));
        Assert.Equal(doc.Serialize(), PanelDocument.Parse(doc.Serialize()).Serialize());
    }

    [Fact]
    public void LegacyBlankThisCountsAsNoImage()
    {
        var doc = PanelDocument.Parse("BveTs Instrument Panel 2.00\r\n[This]\r\nDaytimeImage = this_blank_10x10.png\r\n");
        Assert.True(MainPanel.ThisHasNoImage(doc));
        Assert.Equal(-1, MainPanel.FromThis(doc));
        Assert.False(MainPanel.ThisHasNoImage(PanelDocument.Parse("BveTs Instrument Panel 2.00\r\n[This]\r\nDaytimeImage = panel.png\r\n")));
    }

    [Fact]
    public void FromThisMovesImageAndRaisesOthers()
    {
        var doc = PanelDocument.Parse(Panel);
        doc.This!.PsdLayers.Add("板");
        doc.CropSets.Add(new CropSet { Name = CropSetNames.Base, Day = "panel.psd", DayLayers = ["板"] });
        int at = MainPanel.FromThis(doc);
        Assert.Equal(1, at);                                     // [This] のすぐ後ろ
        var main = doc.MainPanel!;
        Assert.Equal(("panel.png", "panel_N.png", "1"), (main.Get("DaytimeImage"), main.Get("NighttimeImage"), main.Get("Layer")));
        Assert.Equal(["板"], main.PsdLayers);
        Assert.Empty(doc.This.PsdLayers);
        Assert.Equal(((string?)null, (string?)null), (doc.This.Get("DaytimeImage"), doc.This.Get("NighttimeImage")));
        Assert.Equal("500, 300", doc.This.Get("Center"));    // 座標はそのまま
        Assert.Equal(("000000", (string?)null), (main.Get("TransparentColor"), doc.This.Get("TransparentColor"))); // 透過色も移す
        // ほかの計器は全部同じだけ上げて、メインパネルより上に（1→2、3→4）
        Assert.Equal(("2", "4"), (Layer(doc, 2), Layer(doc, 3)));
        // 「本体」の組は表示灯のつながりにする（透明な [This] を切り抜き元から更新で上書きしないように）
        Assert.DoesNotContain(doc.CropSets, c => c.Name == CropSetNames.Base);
        Assert.Equal(CropSetNames.PanelBody, main.CropSet);
    }

    [Fact]
    public void FromThisWithoutImageDoesNothing()
    {
        var doc = PanelDocument.Parse("BveTs Instrument Panel 2.00\r\n[This]\r\nResolution = 100\r\n");
        Assert.Equal(-1, MainPanel.FromThis(doc));
        Assert.Null(doc.MainPanel);
    }

    [Fact]
    public void MakeMainMovesThisImageToTheBottom()
    {
        // よくある既存パネルの形: [This] は背景、メインパネルにする表示灯はレイヤー 2、いちばん下の計器はレイヤー 1
        var doc = PanelDocument.Parse(Panel.Replace("Layer = 1", "Layer = 2") + "\r\n[PilotLamp]\r\nSubject = true\r\nLocation = 0, 0\r\nDaytimeImage = gs.png\r\nLayer = 1\r\n");
        int moved = MainPanel.MakeMain(doc, 1, true);
        Assert.True(doc.Sections[2].IsMainPanel); // 背景は [This] のすぐ後ろ、メインパネルはその次
        Assert.Equal(1, moved);
        var bg = doc.Sections[moved];
        Assert.Equal(("panel", "true", "0", "panel.png", false), (bg.Title, bg.Get("Subject"), bg.Get("Layer"), bg.Get("DaytimeImage"), bg.IsMainPanel));
        Assert.Null(doc.This!.Get("DaytimeImage"));
        Assert.Equal(("2", "1"), (doc.MainPanel!.Get("Layer"), doc.Sections[4].Get("Layer"))); // ほかは変えない
    }

    [Fact]
    public void MakeMainBelowZeroRaisesAll()
    {
        var doc = PanelDocument.Parse(Panel.Replace("Layer = 1", "Layer = 0"));
        int moved = MainPanel.MakeMain(doc, 1, true);
        Assert.Equal("0", doc.Sections[moved].Get("Layer"));
        Assert.Equal(("1", "4"), (doc.MainPanel!.Get("Layer"), doc.Sections[3].Get("Layer")));
    }

    [Fact]
    public void SectionsWithoutLayerAreNotCounted()
    {
        // Layer のない表示灯（中身をコメントにしたものなど）があっても、ほかのレイヤーは動かさない
        var doc = PanelDocument.Parse(Panel + "\r\n[PilotLamp]\r\n;Subject = true\r\n");
        int moved = MainPanel.MakeMain(doc, 1, true);
        Assert.Equal("0", doc.Sections[moved].Get("Layer"));
        Assert.Equal("1", doc.MainPanel!.Get("Layer"));
        Assert.Equal("3", doc.Sections[3].Get("Layer"));
        Assert.Equal(0, MainPanel.RaiseAbove(doc, 0, doc.MainPanel, doc.Sections[moved])); // Layer のない表示灯は 0 と数えない
    }

    [Fact]
    public void MakeMainKeepsThisWhenAsked()
    {
        var doc = PanelDocument.Parse(Panel);
        Assert.Equal(-1, MainPanel.MakeMain(doc, 1, false));
        Assert.Equal("panel.png", doc.This!.Get("DaytimeImage"));
        Assert.Same(doc.Sections[1], doc.MainPanel);
        Assert.Throws<ArgumentException>(() => MainPanel.MakeMain(doc, 2, false)); // 指針はメインパネルにできない
    }
}
