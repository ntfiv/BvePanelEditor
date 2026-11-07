using BvePanelEditor.Core;
using Xunit;

namespace BvePanelEditor.Tests;

/// <summary>「TransparentColor = #0000FF」の「#」は色の値（コメントではない）。</summary>
public sealed class ColorHashTests
{
    [Theory]
    [InlineData("transparentColor = #0000FF", "#0000FF")]
    [InlineData("TransparentColor=#00f", "#00f")]
    [InlineData("Color = #FF0000FF ; 赤", "#FF0000FF")]
    [InlineData("TransparentColor = #0000FF #コメント", "#0000FF")]
    [InlineData("Layer = 5 #abc", "5")]
    [InlineData("Subject = kmph #12345", "kmph")]
    public void HashColorIsValue(string raw, string value)
    {
        var l = PanelLine.Parse(raw);
        Assert.Equal(PanelLineKind.KeyValue, l.Kind);
        Assert.Equal(value, l.Value);
        Assert.Equal(raw, l.Raw);
    }

    [Theory]
    [InlineData("##Layer = 5")]
    [InlineData("#FFFFFF")]
    [InlineData("  # 0000FF")]
    public void OtherHashesAreComments(string raw)
    {
        Assert.Equal(PanelLineKind.Comment, PanelLine.Parse(raw).Kind);
    }

    [Fact]
    public void HeaderTitleAfterHashStillWorks()
    {
        var doc = PanelDocument.Parse("BveTs Instrument Panel 2.00\r\n[PilotLamp]##計器灯\r\nTransparentColor = #0000FF\r\n");
        Assert.Equal("計器灯", doc.Sections[0].Title);
        Assert.Equal("#0000FF", doc.Sections[0].Get("TransparentColor"));
        Assert.True(PanelValues.TryColor(doc.Sections[0].Get("TransparentColor"), out var r, out var g, out var b));
        Assert.Equal((0, 0, 255), (r, g, b));
    }

    [Fact]
    public void ShortColorExpands()
    {
        Assert.True(PanelValues.TryColor("#00F", out var r, out var g, out var b));
        Assert.Equal((0, 0, 255), (r, g, b));
    }
}
