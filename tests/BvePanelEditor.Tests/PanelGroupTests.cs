using BvePanelEditor.Core;
using Xunit;

namespace BvePanelEditor.Tests;

public class PanelGroupTests
{
    // 5 つの計器: [This] A B C D
    const string Plain =
        "BveTs Instrument Panel 2.00\r\n\r\n[This]\r\nResolution = 1024\r\n\r\n" +
        ";A\r\n[PilotLamp]\r\nSubject = ats0\r\nLocation = 10, 10\r\n\r\n" +
        ";B\r\n[PilotLamp]\r\nSubject = ats1\r\nLocation = 20, 20\r\n\r\n" +
        ";C\r\n[PilotLamp]\r\nSubject = ats2\r\nLocation = 30, 30\r\n\r\n" +
        ";D\r\n[PilotLamp]\r\nSubject = ats3\r\nLocation = 40, 40\r\n";

    static string Titles(PanelDocument d) => string.Join("", d.Sections.Skip(1).Select(s => s.Title));
    static string Groups(PanelDocument d) => string.Join(",", d.Sections.Select(s => s.Group ?? "-"));

    [Fact]
    public void NoGroupFilesAreUnchanged()
    {
        Assert.Equal(Plain, PanelDocument.Parse(Plain).Serialize());
    }

    [Fact]
    public void MakeGroupWritesBlockAndRoundTrips()
    {
        var doc = PanelDocument.Parse(Plain);
        var idx = doc.MakeGroup([2, 3], "モニタ");
        Assert.Equal([2, 3], idx);
        var text = doc.Serialize();
        Assert.Contains("\r\n\r\n;===== グループ: モニタ =====\r\n;B\r\n[PilotLamp]", text);
        Assert.Contains("Location = 30, 30\r\n;===== グループ終わり: モニタ =====\r\n\r\n;D", text);

        var back = PanelDocument.Parse(text);
        Assert.Equal(text, back.Serialize());
        Assert.Equal("-,-,モニタ,モニタ,-", Groups(back));
        Assert.Empty(back.GroupWarnings);
        Assert.Equal("BC", string.Join("", back.Members("モニタ").Select(i => back.Sections[i].Title)));
    }

    [Fact]
    public void GroupAtEndOfFile()
    {
        var doc = PanelDocument.Parse(Plain);
        doc.MakeGroup([3, 4], "後ろ");
        var text = doc.Serialize();
        Assert.EndsWith("Location = 40, 40\r\n;===== グループ終わり: 後ろ =====\r\n", text);
        Assert.Equal(text, PanelDocument.Parse(text).Serialize());
    }

    [Fact]
    public void MakeGroupGathersScatteredSections()
    {
        var doc = PanelDocument.Parse(Plain);
        Assert.Equal([2, 3], doc.SectionsReorderedBy([1, 4]));
        var idx = doc.MakeGroup([1, 4], "AD");
        Assert.Equal([1, 2], idx);
        Assert.Equal("ADBC", Titles(doc));
        Assert.Equal(doc.Serialize(), PanelDocument.Parse(doc.Serialize()).Serialize());
    }

    [Fact]
    public void LineMapCountsMarkers()
    {
        var doc = PanelDocument.Parse(Plain);
        doc.MakeGroup([2, 3], "モニタ");
        var lines = BveText.SplitLines(doc.Serialize());
        var map = doc.LineMap();
        for (int i = 0; i < map.Count; i++)
            Assert.Equal(doc.Sections[i].HeaderRaw, lines[map[i].HeaderLine - 1]);
        Assert.Contains(lines[(map[2].FirstLine - 1)..map[2].HeaderLine], l => l.StartsWith(";===== グループ:"));
        Assert.StartsWith(";===== グループ終わり", lines[map[3].LastLine - 1]);
        Assert.Equal(2, doc.SectionAtLine(map[2].FirstLine));
    }

    [Fact]
    public void LooseMarkersAreRead()
    {
        var text = "BveTs Instrument Panel 2.00\r\n;== グループ：ATS ==\r\n[PilotLamp]\r\nSubject = ats0\r\n#グループ終わり\r\n[PilotLamp]\r\nSubject = ats1\r\n";
        var doc = PanelDocument.Parse(text);
        Assert.Equal("ATS,-", Groups(doc));
        Assert.Empty(doc.GroupWarnings);
        // 書き出すと整った形になり、そこからは変わらない
        var canon = doc.Serialize();
        Assert.Contains(";===== グループ: ATS =====", canon);
        Assert.Equal(canon, PanelDocument.Parse(canon).Serialize());
    }

    [Fact]
    public void BrokenMarkersAreWarned()
    {
        var unclosed = PanelDocument.Parse("BveTs Instrument Panel 2.00\r\n;===== グループ: X =====\r\n[PilotLamp]\r\nSubject = a\r\n");
        Assert.Single(unclosed.GroupWarnings);
        Assert.Equal("X", unclosed.Sections[0].Group);

        var stray = PanelDocument.Parse("BveTs Instrument Panel 2.00\r\n[PilotLamp]\r\n;===== グループ終わり: X =====\r\n");
        Assert.Single(stray.GroupWarnings);

        var nested = PanelDocument.Parse("BveTs Instrument Panel 2.00\r\n;===== グループ: X =====\r\n[PilotLamp]\r\n;===== グループ: Y =====\r\n[PilotLamp]\r\n;===== グループ終わり: Y =====\r\n");
        Assert.Single(nested.GroupWarnings);
        Assert.Equal("X,Y", Groups(nested));
    }

    [Fact]
    public void UngroupRenameRemove()
    {
        var doc = PanelDocument.Parse(Plain);
        doc.MakeGroup([1, 2, 3], "G");
        Assert.Equal(3, doc.RemoveFromGroup(2)); // 途中の B はかたまりの後ろへ
        Assert.Equal("ACBD", Titles(doc));
        Assert.Equal("-,G,G,-,-", Groups(doc));

        doc.MakeGroup([4], "G"); // 名前が重なる
        Assert.Equal("G 2", doc.Sections[4].Group);
        Assert.Equal("G 2 2", doc.RenameGroup("G", "G 2")); // 重なれば番号を付ける
        Assert.Equal("モニタ", doc.RenameGroup("G 2 2", "モニタ"));
        doc.Ungroup("モニタ");
        Assert.Equal("-,-,-,-,G 2", Groups(doc));
    }

    [Fact]
    public void MovesKeepGroupsContiguous()
    {
        var doc = PanelDocument.Parse(Plain);
        doc.MakeGroup([2, 3], "G");          // - A [B C] D
        Assert.Equal(3, doc.MoveSection(1, +1)); // A はグループを飛び越える
        Assert.Equal("BCAD", Titles(doc));
        Assert.Equal(1, doc.MoveSection(3, -1)); // 戻る
        Assert.Equal("ABCD", Titles(doc));
        Assert.Equal(2, doc.MoveSection(2, -1)); // グループの中の B はグループの外へ出ない
        Assert.Equal(3, doc.MoveSection(2, +1)); // 中では動く
        Assert.Equal("ACBD", Titles(doc));

        Assert.Equal(3, doc.MoveGroup("G", +1)); // かたまりごと D の後ろへ
        Assert.Equal("ADCB", Titles(doc));
        Assert.Equal(2, doc.MoveGroup("G", -1));
        Assert.Equal("ACBD", Titles(doc));
    }

    [Fact]
    public void DuplicateAndDeleteGroup()
    {
        var doc = PanelDocument.Parse(Plain);
        doc.MakeGroup([2, 3], "モニタ");
        var copies = doc.DuplicateGroup("モニタ", "モニタ のコピー", 10, 5);
        Assert.Equal([4, 5], copies);
        Assert.Equal("30, 25", doc.Sections[4].Get("Location"));
        Assert.Equal("モニタ のコピー", doc.Sections[5].Group);
        var back = PanelDocument.Parse(doc.Serialize());
        Assert.Equal(2, back.GroupNames().Count);
        Assert.Equal(2, back.DeleteGroup("モニタ"));
        Assert.Equal(5, back.Sections.Count);
    }

    [Fact]
    public void TitleIsNotConfusedWithMarker()
    {
        var doc = PanelDocument.Parse(Plain);
        doc.MakeGroup([2], "G");
        var back = PanelDocument.Parse(doc.Serialize());
        Assert.Equal("B", back.Sections[2].Title);
        back.Sections[3].SetTitle(""); // 名前のない計器
        back.MakeGroup([3], "H");
        var again = PanelDocument.Parse(back.Serialize());
        Assert.Equal("", again.Sections[3].Title); // 開始行は名前として読まない
    }
}
