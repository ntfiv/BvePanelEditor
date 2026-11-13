using System.Text;
using BvePanelEditor.Core;
using Xunit;

namespace BvePanelEditor.Tests;

public class PanelDocumentTests
{
    const string Sample = "BveTs Instrument Panel 2.00\r\n\r\n;Panel本体\r\n\r\n[This]\r\nResolution = 1024\r\nDaytimeImage = panel.png ; 昼\r\nCenter = 512, 300\r\n\r\n;;次駅停車\r\n[PilotLamp]\r\nSubject = ats22\r\nLocation = 374, 1344\r\nDaytimeImage = nextstop.png\r\nLayer = 5\r\n\r\n[Needle]\r\nsubject=kmph\r\nLocation = 100, 200\r\n";

    [Fact]
    public void RoundTripIsExact()
    {
        var doc = PanelDocument.Parse(Sample);
        Assert.Equal(Sample, doc.Serialize());
        Assert.Equal(3, doc.Sections.Count);
        Assert.Equal("2.00", doc.Version);
    }

    [Fact]
    public void TitleIsCommentRightAboveHeader()
    {
        var doc = PanelDocument.Parse(Sample);
        Assert.Equal("次駅停車", doc.Sections[1].Title);
        Assert.Equal("", doc.Sections[0].Title); // 空行をはさんだコメントは名前にしない
        Assert.Equal("", doc.Sections[2].Title);
    }

    [Theory]
    [InlineData(";;次駅停車", "次駅停車")]
    [InlineData(";----------------------<    ATS電源", "ATS電源")]
    [InlineData(";----------------------<    ATS関連    >----------------------", "ATS関連")]
    [InlineData(";速度計 (km/h)", "速度計 (km/h)")]
    [InlineData(";_pos_wm_base", "_pos_wm_base")]   // 1 文字の記号は名前の一部
    [InlineData(";-ATS", "-ATS")]
    [InlineData(";非常!!", "非常!!")]
    [InlineData(";=== 速度計 ===", "速度計")]
    [InlineData("  # 速度計 ", "速度計")]
    [InlineData(";【ATS】確認", "【ATS】確認")]
    [InlineData(";Layer = 5", "")]
    [InlineData(";////////////", "")]
    [InlineData("[PilotLamp]##計器灯", "計器灯")]
    public void TitleParsing(string raw, string expected)
    {
        PanelSection.TrySplitTitle(raw, out _, out var t);
        Assert.Equal(expected, PanelSection.TrySplitTitle(raw, out _, out _) ? t : "");
    }

    [Fact]
    public void SetTitleKeepsMarks()
    {
        var doc = PanelDocument.Parse(Sample);
        doc.Sections[1].SetTitle("次駅停車灯");
        Assert.Contains(";;次駅停車灯\r\n[PilotLamp]", doc.Serialize());

        doc.Sections[2].SetTitle("速度計");
        Assert.Contains("\r\n;速度計\r\n[Needle]", doc.Serialize());
        Assert.Equal("速度計", PanelDocument.Parse(doc.Serialize()).Sections[2].Title);

        doc.Sections[2].SetTitle("");
        doc.Sections[1].SetTitle("次駅停車");
        Assert.Equal(Sample, doc.Serialize());
    }

    [Fact]
    public void HeaderTitleWinsOverGroupDivider()
    {
        const string text = "BveTs Instrument Panel 2.00\r\n\r\n;-----<    ATS関連    >-----\r\n[PilotLamp]##ATS電源\r\nSubject = ats0\r\n\r\n;-----<  表示灯類  >-----\r\n[PilotLamp]\r\nSubject = door\r\n";
        var doc = PanelDocument.Parse(text);
        Assert.Equal("ATS電源", doc.Sections[0].Title);
        Assert.Equal("表示灯類", doc.Sections[1].Title);
        doc.Sections[1].SetTitle("戸閉灯");
        Assert.Contains(";-----<  戸閉灯  >-----\r\n[PilotLamp]", doc.Serialize());
        doc.Sections[1].SetTitle("表示灯類");
        Assert.Equal(text, doc.Serialize());
    }

    [Fact]
    public void HeaderLineTitle()
    {
        var doc = PanelDocument.Parse("BveTs Instrument Panel 2.00\r\n\r\n[PilotLamp]##計器灯\r\nSubject = true\r\n");
        var s = doc.Sections[0];
        Assert.Equal("計器灯", s.Title);
        Assert.Equal("PilotLamp", s.Name);
        s.SetTitle("計器灯2");
        Assert.Equal("[PilotLamp]##計器灯2", s.HeaderRaw);
        s.SetTitle("");
        Assert.Equal("[PilotLamp]", s.HeaderRaw);
        s.SetTitle("新しい名前");
        Assert.Equal(";新しい名前", s.Leading[^1].Raw);
    }

    [Fact]
    public void SetKeepsFormattingAndComment()
    {
        var doc = PanelDocument.Parse(Sample);
        doc.Sections[0].Set("DaytimeImage", "panel2.png");
        Assert.Contains("DaytimeImage = panel2.png ; 昼\r\n", doc.Serialize());
        doc.Sections[2].Set("SUBJECT", "bc");
        Assert.Contains("subject=bc\r\n", doc.Serialize());
    }

    [Fact]
    public void SetAddsBeforeTrailingBlank()
    {
        var doc = PanelDocument.Parse(Sample);
        doc.Sections[1].Set("TransparentColor", "000000");
        var s = doc.Serialize();
        Assert.Contains("Layer = 5\r\nTransparentColor = 000000\r\n\r\n[Needle]", s);
    }

    [Fact]
    public void RemoveKey()
    {
        var doc = PanelDocument.Parse(Sample);
        doc.Sections[1].Set("Layer", "");
        Assert.DoesNotContain("Layer = 5", doc.Serialize());
    }

    [Fact]
    public void LineMapPointsAtHeaders()
    {
        var doc = PanelDocument.Parse(Sample);
        var lines = BveText.SplitLines(doc.Serialize());
        var map = doc.LineMap();
        for (int i = 0; i < map.Count; i++)
            Assert.Equal(doc.Sections[i].HeaderRaw, lines[map[i].HeaderLine - 1]);
        Assert.Equal(1, doc.SectionAtLine(map[1].HeaderLine + 1));
    }

    [Fact]
    public void CreateNewHasThis()
    {
        var doc = PanelDocument.CreateNew("img/panel.png", 2048, 1536);
        Assert.Single(doc.Sections);
        Assert.Equal(SectionKind.This, doc.Sections[0].Kind);
        Assert.Equal("img/panel.png", doc.This!.Get("DaytimeImage"));
        Assert.Equal("1024, 768", doc.This.Get("Center"));
        Assert.Equal(doc.Serialize(), PanelDocument.Parse(doc.Serialize()).Serialize());
    }

    [Fact]
    public void BomWinsOverHeaderEncoding()
    {
        var text = "BveTs Instrument Panel 2.01:ISO_8859-1\n;日本語\n";
        var bytes = BveText.Encode(text, new UTF8Encoding(false), true);
        var (decoded, info) = BveText.Decode(bytes);
        Assert.Equal(text, decoded);
        Assert.Equal(bytes, BveText.Encode(decoded, BveText.ResolveSaveEncoding(decoded, info), info.HasBom));
    }

    [Fact]
    public void SubjectDigits()
    {
        var st = new Dictionary<string, double> { ["kmph"] = 87.6, ["am"] = -300 };
        Assert.Equal(7, Subjects.Resolve("kmphd0", st));
        Assert.Equal(8, Subjects.Resolve("KMPHD1", st));
        Assert.Equal(0, Subjects.Resolve("kmphd2", st));
        Assert.Equal(300, Subjects.Resolve("amabs", st));
        Assert.Equal(1, Subjects.Resolve("true", st));
        Assert.Equal("kmph", Subjects.BaseKey("kmphd1"));
    }

    [Fact]
    public void ShiftJisHeaderIsHonored()
    {
        BveText.EnsureInitialized();
        var text = "BveTs Instrument Panel 2.00:shift_jis\r\n;日本語\r\n";
        var bytes = BveText.ShiftJis.GetBytes(text);
        var (decoded, info) = BveText.Decode(bytes);
        Assert.Equal(text, decoded);
        Assert.Equal(932, info.Encoding.CodePage);
    }

    [Fact]
    public void ShiftJisWithoutHeaderFallsBack()
    {
        BveText.EnsureInitialized();
        var text = "BveTs Instrument Panel 2.00\r\n;日本語\r\n";
        var (decoded, info) = BveText.Decode(BveText.ShiftJis.GetBytes(text));
        Assert.Equal(text, decoded);
        Assert.True(info.EncodingFallback);
    }

    [Fact]
    public void LegacyVersionHeaderIsPanel()
    {
        const string text = "Version 1.0\r\n\r\n[This]\r\nResolution = 975\r\nDaytimeImage = Panel.png\r\n";
        Assert.Equal(BveFileKind.Panel, BveFiles.Detect(text));
        var doc = PanelDocument.Parse(text);
        Assert.True(doc.IsLegacyHeader);
        Assert.False(doc.SupportsV201);
        Assert.Equal(text, doc.Serialize());
        Assert.Equal(BveFileKind.Unknown, BveFiles.Detect("Version 1.0\r\nfoo = 1\r\n")); // [This] がなければパネルとみなさない
    }

    /// <summary>
    /// BVE_PANEL_DIR を設定すると、その下の全パネルファイルで読み書きが元と一致するか確かめる（ファイルは書き換えない）。
    /// </summary>
    [Fact]
    public void RealPanelsRoundTrip()
    {
        var dir = Environment.GetEnvironmentVariable("BVE_PANEL_DIR");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        int count = 0;
        var failures = new List<string>();
        foreach (var f in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories).Where(p => p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)))
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(f); } catch (IOException) { continue; }
            var (text, info) = BveText.Decode(bytes);
            if (BveFiles.Detect(text) != BveFileKind.Panel) continue;
            count++;
            var doc = PanelDocument.Parse(text);
            var back = BveText.Encode(doc.Serialize(), BveText.ResolveSaveEncoding(text, info), info.HasBom);
            if (!bytes.AsSpan().SequenceEqual(back)) failures.Add(f);
        }
        Assert.True(failures.Count == 0, $"{count} 件中 {failures.Count} 件不一致:\n" + string.Join("\n", failures));
        Console.WriteLine($"{count} panels OK");
    }

    [Fact]
    public void SetValueWithNewlineDoesNotBreakLines()
    {
        // 値に改行が入っても行が割れない（空白に置き換える。SetTitle と同じ扱い）
        var doc = PanelDocument.Parse(Sample);
        doc.Sections[1].Set("DaytimeImage", "a\nb.png");
        var text = doc.Serialize();
        Assert.Equal("a b.png", PanelDocument.Parse(text).Sections[1].Get("DaytimeImage"));
        Assert.Equal(text, PanelDocument.Parse(text).Serialize()); // 書き出しが安定する
        Assert.Equal(3, PanelDocument.Parse(text).Sections.Count);
    }

    [Fact]
    public void SetNewKeyWithCrLfValueDoesNotBreakLines()
    {
        var doc = PanelDocument.Parse(Sample);
        doc.Sections[1].Set("Comment", "x\r\ny\rz");
        var text = doc.Serialize();
        Assert.Equal("x  y z", PanelDocument.Parse(text).Sections[1].Get("Comment"));
        Assert.Equal(text, PanelDocument.Parse(text).Serialize());
    }
}
