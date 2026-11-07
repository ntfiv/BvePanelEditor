using BvePanelEditor.Core;
using BvePanelEditor.Core.Crop;
using BvePanelEditor.Core.Psd;
using Xunit;

namespace BvePanelEditor.Tests;

/// <summary>画像からパネルを作るで、PSD のレイヤーを 4 点の画像に振り分ける。</summary>
public sealed class CropLayerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "panelkun_croplayer_" + Guid.NewGuid().ToString("N"));
    public CropLayerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    static readonly CropSlotKind[] Four = [new(false, false), new(false, true), new(true, false), new(true, true)];
    static readonly PsdRect Lamp = new(10, 10, 20, 16);

    /// <summary>昼も夜も 1 つに入った PSD（下から: 板、ATS_TT（隠れ）、夜フォルダ[暗く、点灯]）。</summary>
    private string OnePsd(byte boardRed = 50)
    {
        var p = Path.Combine(_dir, "panel.psd");
        TestPsdWriter.Write(p, 100, 60,
        [
            TestLayer.Solid("板", new PsdRect(0, 0, 100, 60), boardRed, 50, 50),
            new TestLayer { Name = "ATS_TT", Bounds = Lamp, Bgra = TestLayer.Solid("x", Lamp, 255, 0, 0).Bgra, Hidden = true },
            TestLayer.FolderEnd(),
            TestLayer.Solid("暗く", new PsdRect(0, 0, 100, 60), 10, 10, 10, 200),
            new TestLayer { Name = "点灯", Bounds = Lamp, Bgra = TestLayer.Solid("x", Lamp, 200, 0, 0).Bgra },
            new TestLayer { Name = "夜", SectionType = 1, Hidden = true },
        ]);
        return p;
    }

    [Theory]
    [InlineData("ATS_TT", true, false)]
    [InlineData("表示灯/発光", true, false)]
    [InlineData("lamp on", true, false)]
    [InlineData("Button", false, false)]
    [InlineData("夜/暗く", false, true)]
    [InlineData("panel_N", false, true)]
    [InlineData("Night/ATS_TT", true, true)]
    [InlineData("Panel", false, false)]
    [InlineData("carN", false, true)]
    [InlineData("car_drN", false, true)]
    [InlineData("BVEPanel_N_TT", true, true)]
    [InlineData("光/DK/hikari", true, false)]
    [InlineData("ON", true, false)]
    [InlineData("glow", true, false)]
    [InlineData("Panel/Glow", true, false)]
    [InlineData("glow_2", true, false)]
    [InlineData("night glow", true, true)]
    public void Names(string path, bool lit, bool night)
    {
        Assert.Equal(lit, CropLayers.IsLitName(path));
        Assert.Equal(night, CropLayers.IsNightName(path));
    }

    [Theory]
    [InlineData("glow night", "glow")]
    [InlineData("Night/glow", "glow")]
    [InlineData("panel_night", "panel")]
    [InlineData("メーター 夜", "メーター")]
    [InlineData("Panel/base-Night", "panel/base")]
    [InlineData("car_Night_glow", "car_glow")]
    [InlineData("glow", null)]
    public void NightPairKeys(string path, string? key) => Assert.Equal(key, CropLayers.NightPairKey(path));

    [Fact]
    public void CarStyleDayAndNight()
    {
        // よくある名前の付け方の並び（下から）。重ねる画像として昼・夜の 2 欄に振り分ける
        var p = Path.Combine(_dir, "car.psd");
        var all = new PsdRect(0, 0, 100, 60);
        TestPsdWriter.Write(p, 100, 60,
        [
            TestLayer.Solid("car_bg_Night", all, 1, 1, 1),
            TestLayer.Solid("car_bg", all, 2, 2, 2),
            new TestLayer { Name = "car_door_Night", Bounds = Lamp, Bgra = TestLayer.Solid("x", Lamp, 3, 3, 3).Bgra, Hidden = true },
            TestLayer.Solid("car_Night", all, 4, 4, 4),
            new TestLayer { Name = "car_Night_glow", Bounds = Lamp, Bgra = TestLayer.Solid("x", Lamp, 5, 5, 5).Bgra },
            new TestLayer { Name = "car_door", Bounds = Lamp, Bgra = TestLayer.Solid("x", Lamp, 6, 6, 6).Bgra, Hidden = true },
            TestLayer.Solid("car", all, 7, 7, 7),
            new TestLayer { Name = "car_glow", Bounds = Lamp, Bgra = TestLayer.Solid("x", Lamp, 8, 8, 8).Bgra },
        ]);
        using var psd = PsdFile.Open(p);
        var g = CropLayers.Guess(psd, [new(false, false), new(true, false)]);
        Assert.Equal(["car_bg", "car", "car_glow"], g[0]);
        Assert.Equal(["car_bg_Night", "car_Night", "car_Night_glow"], g[1]);
    }

    [Fact]
    public void LayerWithNightPairIsDayOnly()
    {
        // 「base」と「base night」、「glow」と「glow night」は昼・夜の対。対のない「frame」は昼と夜の両方に使う
        var p = Path.Combine(_dir, "pair.psd");
        var all = new PsdRect(0, 0, 100, 60);
        TestPsdWriter.Write(p, 100, 60,
        [
            TestLayer.Solid("frame", all, 1, 1, 1),
            TestLayer.Solid("base", all, 200, 200, 200),
            TestLayer.Solid("base night", all, 20, 20, 20),
            new TestLayer { Name = "glow", Bounds = Lamp, Bgra = TestLayer.Solid("x", Lamp, 255, 0, 0).Bgra },
            new TestLayer { Name = "glow night", Bounds = Lamp, Bgra = TestLayer.Solid("x", Lamp, 255, 0, 0).Bgra },
        ]);
        using var psd = PsdFile.Open(p);
        var g = CropLayers.Guess(psd, Four);
        Assert.Equal(["frame", "base"], g[0]);                       // 昼・無発光
        Assert.Equal(["frame", "base", "glow"], g[1]);               // 昼・発光
        Assert.Equal(["frame", "base night"], g[2]);                 // 夜・無発光（対のある base は入れない）
        Assert.Equal(["frame", "base night", "glow night"], g[3]);   // 夜・発光
    }

    [Fact]
    public void GuessSplitsByName()
    {
        using var psd = PsdFile.Open(OnePsd());
        var g = CropLayers.Guess(psd, Four);
        Assert.Equal(["板"], g[0]);
        Assert.Equal(["板", "ATS_TT"], g[1]);
        Assert.Equal(["板", "夜/暗く"], g[2]);
        Assert.Equal(["板", "夜/暗く", "夜/点灯"], g[3]);       // 夜らしい発光があるので、昼の ATS_TT は使わない

        // 1 つの欄だけに使う PSD なら、表示されているレイヤーそのまま
        var only = CropLayers.Guess(psd, [new(false, true)]);
        Assert.Equal(["板"], only[0]);
    }

    [Fact]
    public void WholeLitImageIsUsedAlone()
    {
        // 発光した絵をまるごと入れた PSD（下から: 板_TT（隠れ）、板_N_TT（隠れ）、板、板N）
        var p = Path.Combine(_dir, "whole.psd");
        var full = new PsdRect(0, 0, 100, 60);
        TestPsdWriter.Write(p, 100, 60,
        [
            new TestLayer { Name = "板_TT", Bounds = full, Bgra = TestLayer.Solid("x", full, 255, 0, 0).Bgra, Hidden = true },
            new TestLayer { Name = "板_N_TT", Bounds = full, Bgra = TestLayer.Solid("x", full, 128, 0, 0).Bgra, Hidden = true },
            TestLayer.Solid("板", full, 50, 50, 50),
            TestLayer.Solid("板N", full, 20, 20, 20),
        ]);
        using var psd = PsdFile.Open(p);
        var g = CropLayers.Guess(psd, Four);
        Assert.Equal(["板"], g[0]);
        Assert.Equal(["板_TT"], g[1]);
        Assert.Equal(["板", "板N"], g[2]);
        Assert.Equal(["板_N_TT"], g[3]);
    }

    [Fact]
    public void ComposeUsesPickedLayersOnly()
    {
        using var psd = PsdFile.Open(OnePsd());
        var missing = new List<string>();
        var img = CropLayers.Compose(psd, ["板", "ATS_TT", "ない"], null, missing);
        Assert.Equal(["ない"], missing);
        Assert.Equal((100, 60), (img.Width, img.Height));
        int i = (12 * 100 + 12) * 4;
        Assert.Equal(255, img.Bgra[i + 2]);              // 隠れていても選んだので重なる
        var folder = CropLayers.Compose(psd, ["板", "夜"]);  // フォルダは中身ごと
        Assert.Equal(200, folder.Bgra[i + 2]);
    }

    private CropBuildSpec LayeredSpec(PsdFile psd, string psdPath)
    {
        var g = CropLayers.Guess(psd, Four);
        var px = g.Select(l => CropLayers.Compose(psd, l)).ToArray();
        return new CropBuildSpec
        {
            PanelDir = _dir, OutputDir = Path.Combine(_dir, "crop"),
            UnlitDayPath = psdPath, UnlitDayLayers = g[0], UnlitDay = px[0],
            UnlitNightPath = psdPath, UnlitNightLayers = g[2], UnlitNight = px[2],
            Width = 100, Height = 60,
            Sets = [new CropSetImages { DayPath = psdPath, Day = px[1], DayLayers = g[1], NightPath = psdPath, Night = px[3], NightLayers = g[3] }],
            Regions = [new CropRegion { Rect = Lamp, Name = "ATS", Subject = "ats0" }],
        };
    }

    [Fact]
    public void BuildWritesBaseAndRemembersLayers()
    {
        var path = OnePsd();
        using var psd = PsdFile.Open(path);
        var r = CropBuild.Build(LayeredSpec(psd, path));
        Assert.Equal(Path.Combine("crop", "panel.png"), r.BaseDay);
        Assert.Equal(Path.Combine("crop", "panel_N.png"), r.BaseNight);
        Assert.Equal(4, r.Files.Count);                       // 本体 2 + 表示灯 2（[This] の画像は作らない）
        Assert.True(PngWriter.IsPanelKunPngBytes(r.Files[0].Png));
        var baseSet = r.Sets[0];
        Assert.Equal(CropSetNames.PanelBody, baseSet.Name);   // メインパネルの表示灯のつながり
        Assert.Equal(CropSetNames.PanelBody, r.Main!.CropSet);
        Assert.Equal("panel.psd", baseSet.Day);
        Assert.Equal(["板"], baseSet.DayLayers);

        var doc = PanelDocument.CreateNew(null, 100, 60);
        doc.CropSets.AddRange(r.Sets);
        doc.Sections.AddRange(r.Sections);
        var text = doc.Serialize();
        Assert.Contains(";===== 切り抜き元 パネル本体: 昼=panel.psd | 夜=panel.psd =====\r\n;@切り抜き元レイヤー パネル本体 昼: 板\r\n;@切り抜き元レイヤー パネル本体 夜: 板 | 夜/暗く\r\n", text);
        Assert.Contains(";@切り抜き元レイヤー 発光 夜: 板 | 夜/暗く | 夜/点灯\r\n", text);
        var back = PanelDocument.Parse(text);
        Assert.Equal(text, back.Serialize());
        Assert.Empty(back.GroupWarnings);
        Assert.Equal(["板", "夜/暗く", "夜/点灯"], back.CropSets[1].NightLayers);
        Assert.True(back.HasCropLink);
    }

    [Fact]
    public void LayerLineWithoutSetIsIgnored()
    {
        var doc = PanelDocument.Parse("BveTs Instrument Panel 2.00\r\n;@切り抜き元レイヤー 扉 昼: a\r\n");
        Assert.Single(doc.GroupWarnings);
        Assert.Empty(doc.CropSets);
    }

    [Fact]
    public void UpdateRecomposesFromPsd()
    {
        var path = OnePsd();
        CropBuildResult r;
        using (var psd = PsdFile.Open(path)) r = CropBuild.Build(LayeredSpec(psd, path));
        foreach (var f in r.Files) { Directory.CreateDirectory(Path.GetDirectoryName(f.FullPath)!); File.WriteAllBytes(f.FullPath, f.Png); }
        var doc = PanelDocument.CreateNew(null, 100, 60);
        doc.CropSets.AddRange(r.Sets);
        doc.Sections.AddRange(r.Sections);

        // すぐ更新しても同じ（透明な [This] は作り直さない）
        var same = CropUpdate.Plan(doc, _dir, _ => null);
        Assert.Empty(same.Missing);
        Assert.Equal(4, same.Files.Count);
        foreach (var f in same.Files) Assert.Equal(File.ReadAllBytes(f.FullPath), f.Png);

        // PSD の板の色を変えると、本体も表示灯も作り直す
        OnePsd(boardRed: 120);
        var plan = CropUpdate.Plan(doc, _dir, _ => null);
        Assert.Equal(4, plan.Files.Count);
        var day = TestPngReader.Read(plan.Files.First(f => f.FullPath.EndsWith("panel.png")).Png);
        Assert.Equal(120, day.Rgba[0]);

        // 手で差し替えた本体は飛ばす
        File.WriteAllBytes(Path.Combine(_dir, r.BaseDay), StripText(File.ReadAllBytes(Path.Combine(_dir, r.BaseDay))));
        var plan2 = CropUpdate.Plan(doc, _dir, _ => null);
        Assert.Single(plan2.Skipped);
        Assert.Equal(2, plan2.Files.Count);                  // メインパネルは昼・夜とも飛ばす

        // レイヤーがなくなったら知らせる
        doc.CropSets[1].DayLayers = ["板", "消えた"];
        var plan3 = CropUpdate.Plan(doc, _dir, _ => null);
        Assert.Contains(plan3.Warnings, w => w.Contains("消えた"));
    }

    private static byte[] StripText(byte[] png)
    {
        var o = new MemoryStream();
        o.Write(png, 0, 8);
        int pos = 8;
        while (pos < png.Length)
        {
            int len = png[pos] << 24 | png[pos + 1] << 16 | png[pos + 2] << 8 | png[pos + 3];
            if (System.Text.Encoding.ASCII.GetString(png, pos + 4, 4) != "tEXt") o.Write(png, pos, 12 + len);
            pos += 12 + len;
        }
        return o.ToArray();
    }
}
