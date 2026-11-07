using BvePanelEditor.Core;
using Xunit;

namespace BvePanelEditor.Tests;

/// <summary>コメントアウトして無効にした計器（BVE でも表示されない）。</summary>
public sealed class DisabledSectionTests
{
    private const string E217Style = "BveTs Instrument Panel 2.01\r\n\r\n[This]\r\nResolution = 3840\r\nDaytimeImage = Main.png\r\n\r\n"
        + "[Needle]\t\t\t\t; 速度計の針\r\nSubject\t\t= kmph\r\nLocation\t= 1353, 3499\r\nTilt\t\t= 47, 0\r\n\r\n"
        + ";[Needle] ; 調整用画像\r\n;Subject = true ; 表示する状態量\r\n;Location = 1357, 3501 ; 中心\r\n;DaytimeImage = scale_speed.png\r\n;Tilt = 47, 0\r\n\r\n"
        + "[PilotLamp]\t\t\t\t; 圧力計中心ピン\r\nSubject = true\r\nLocation = 497, 3543\r\n";

    [Fact]
    public void ReadsCommentedOutSectionAsDisabled()
    {
        var doc = PanelDocument.Parse(E217Style);
        Assert.Equal(4, doc.Sections.Count);
        var s = doc.Sections[2];
        Assert.True(s.Disabled);
        Assert.Equal(SectionKind.Needle, s.Kind);
        Assert.Equal("調整用画像", s.Title);
        Assert.Equal("scale_speed.png", s.Get("DaytimeImage"));
        Assert.Equal("1357, 3501", s.Get("Location"));
        // 次の表示灯の名前は表示灯に付く
        Assert.False(doc.Sections[3].Disabled);
        Assert.Equal("圧力計中心ピン", doc.Sections[3].Title);
        Assert.Equal(E217Style, doc.Serialize());
    }

    [Fact]
    public void DisableAndEnableRoundTrip()
    {
        var doc = PanelDocument.Parse(E217Style);
        doc.Sections[1].Disable();
        var text = doc.Serialize();
        Assert.Contains(";[Needle]\t\t\t\t; 速度計の針\r\n;Subject\t\t= kmph\r\n;Location\t= 1353, 3499\r\n;Tilt\t\t= 47, 0\r\n\r\n;[Needle] ; 調整用画像", text);
        // 読み直しても無効のまま
        var again = PanelDocument.Parse(text);
        Assert.True(again.Sections[1].Disabled);
        Assert.True(again.Sections[2].Disabled);
        Assert.Equal(text, again.Serialize());
        // 戻すと元のとおり
        again.Sections[1].Enable();
        Assert.Equal(E217Style, again.Serialize());
        // 調整用画像を有効にすると「;」が外れる
        var doc2 = PanelDocument.Parse(E217Style);
        doc2.Sections[2].Enable();
        Assert.Contains("\r\n[Needle] ; 調整用画像\r\nSubject = true ; 表示する状態量\r\n", doc2.Serialize());
    }

    [Fact]
    public void BlankLinesInsideAndMetaLinesSurvive()
    {
        var text = "BveTs Instrument Panel 2.00\r\n\r\n[This]\r\nDaytimeImage = a.png\r\n\r\n"
            + ";===== グループ: 計器 =====\r\n[PilotLamp]\r\n;@切り抜き: 発光\r\nSubject = door\r\n\r\nLocation = 1, 2\r\n;===== グループ終わり: 計器 =====\r\n\r\n"
            + "[Needle]\r\nSubject = bc\r\n";
        var doc = PanelDocument.Parse(text);
        Assert.Equal(text, doc.Serialize());
        doc.Sections[1].Disable();
        var off = doc.Serialize();
        Assert.Contains(";[PilotLamp]\r\n;@切り抜き: 発光\r\n;Subject = door\r\n;\r\n;Location = 1, 2\r\n;===== グループ終わり: 計器 =====\r\n\r\n[Needle]", off);
        var again = PanelDocument.Parse(off);
        Assert.True(again.Sections[1].Disabled);
        Assert.Equal("計器", again.Sections[1].Group);
        Assert.Equal("発光", again.Sections[1].CropSet);
        Assert.Equal("1, 2", again.Sections[1].Get("Location"));
        Assert.False(again.Sections[2].Disabled);
        Assert.Equal(off, again.Serialize());
        again.Sections[1].Enable();
        Assert.Equal(text, again.Serialize());
    }

    [Fact]
    public void EditingDisabledSectionKeepsItCommented()
    {
        var doc = PanelDocument.Parse(E217Style);
        doc.Sections[2].Set("Location", "1360, 3500");
        doc.Sections[2].Set("Layer", "15");
        var text = doc.Serialize();
        Assert.Contains(";Location = 1360, 3500 ; 中心", text);
        Assert.Contains(";Layer = 15", text);
        Assert.DoesNotContain("\nLayer = 15", text);
    }

    [Fact]
    public void ThisAndOrdinaryCommentsAreNotSections()
    {
        var text = "BveTs Instrument Panel 2.00\r\n\r\n;[This]\r\n;[Foo]\r\n; [Needle] は使わない\r\n[This]\r\nDaytimeImage = a.png\r\n";
        var doc = PanelDocument.Parse(text);
        Assert.Single(doc.Sections);
        Assert.Equal(text, doc.Serialize());
    }
}
