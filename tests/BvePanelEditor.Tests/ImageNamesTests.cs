using BvePanelEditor.Core;
using Xunit;

namespace BvePanelEditor.Tests;

/// <summary>画像のファイル名の決まり（連番・夜の画像）。</summary>
public sealed class ImageNamesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "panelkun_names_" + Guid.NewGuid().ToString("N"));
    public ImageNamesTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string F(string name) { var p = Path.Combine(_dir, name); File.WriteAllBytes(p, [0]); return p; }
    private static List<string> Names(IEnumerable<string> paths) => paths.Select(p => Path.GetFileName(p)!).ToList();

    [Fact]
    public void SequenceFindsNumberedFilesInOrder()
    {
        var a0 = F("ID_motor_0.png"); F("ID_motor_1.png"); F("ID_motor_2.png"); F("ID_motor_10.png");
        F("ID_motor_0_N.png"); F("ID_motor_N_1.png"); F("ID_motor_1.bmp"); F("other_3.png"); F("ID_motor_05.png");
        Assert.Equal(["ID_motor_0.png", "ID_motor_1.png", "ID_motor_2.png", "ID_motor_10.png"], Names(ImageNames.FindSequence(a0)));
    }

    [Fact]
    public void ZeroPaddedSequenceKeepsWidth()
    {
        var d01 = F("seg01.png"); F("seg02.png"); F("seg10.png"); F("seg1.png"); F("seg003.png");
        Assert.Equal(["seg01.png", "seg02.png", "seg10.png"], Names(ImageNames.FindSequence(d01)));
        var d001 = F("n001.png"); F("n002.png"); F("n01.png");
        Assert.Equal(["n001.png", "n002.png"], Names(ImageNames.FindSequence(d001)));
        var single = F("panel.png");
        Assert.Equal(["panel.png"], Names(ImageNames.FindSequence(single)));
    }

    [Theory]
    [InlineData("d_0.png", "d_0_N.png")]
    [InlineData("d_0.png", "d_0N.png")]
    [InlineData("d_0.png", "d_0_night.png")]
    [InlineData("d_0.png", "d_0night.png")]
    [InlineData("d_0.png", "d_0_dark.png")]
    [InlineData("d_0.png", "d_N_0.png")]
    [InlineData("d_0.png", "dN_0.png")]
    [InlineData("d_0.png", "d_night_0.png")]
    [InlineData("panel.png", "panel_dark.png")]
    [InlineData("panel.png", "panelN.png")]
    public void NightIsFoundByName(string day, string night)
    {
        var d = F(day);
        F(night);
        Assert.Equal(night, Path.GetFileName(ImageNames.FindNight(d)));
    }

    [Fact]
    public void NoNightGivesNull() => Assert.Null(ImageNames.FindNight(F("solo_3.png")));
}
