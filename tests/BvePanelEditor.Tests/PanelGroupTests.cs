using BvePanelEditor.Core;
using Xunit;

namespace BvePanelEditor.Tests;

public class PanelGroupTests
{
    // 5 つのオブジェクト: [This] A B C D
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
        // 入れ子は使える（外側の X の終わりがないことだけを知らせる）
        Assert.Single(nested.GroupWarnings);
        Assert.Equal("X,Y", Groups(nested));
        Assert.Equal(["X", "Y"], nested.Sections[1].Groups);

        // 外側の終わりで、終わりのない内側のグループも閉じる
        var outerEnd = PanelDocument.Parse("BveTs Instrument Panel 2.00\r\n;===== グループ: X =====\r\n[PilotLamp]\r\n;===== グループ: Y =====\r\n[PilotLamp]\r\n;===== グループ終わり: X =====\r\n[PilotLamp]\r\n");
        Assert.Single(outerEnd.GroupWarnings);
        Assert.Equal("X,Y,-", Groups(outerEnd));
    }

    static string Paths(PanelDocument d) => string.Join(",", d.Sections.Skip(1).Select(s => s.Title + (s.Groups.Count > 0 ? "[" + string.Join("/", s.Groups) + "]" : "")));

    [Fact]
    public void NestedGroupsRoundTrip()
    {
        // A [G1: B [G2: C] D] → ファイルは入れ子の開始行・終了行で書く
        var doc = PanelDocument.Parse(Plain);
        doc.MakeGroup([2, 3, 4], "G1");
        var inner = doc.MakeGroup([3], "G2");
        Assert.Equal([3], inner);
        Assert.Equal("A,B[G1],C[G1/G2],D[G1]", Paths(doc));
        var text = doc.Serialize();
        Assert.Contains(";===== グループ: G1 =====\r\n;B", text);
        Assert.Contains("Location = 20, 20\r\n\r\n;===== グループ: G2 =====\r\n;C\r\n", text);
        Assert.Contains("Location = 30, 30\r\n;===== グループ終わり: G2 =====\r\n\r\n;D", text);
        Assert.Contains("Location = 40, 40\r\n;===== グループ終わり: G1 =====", text);
        var back = PanelDocument.Parse(text);
        Assert.Empty(back.GroupWarnings);
        Assert.Equal(text, back.Serialize());
        Assert.Equal("A,B[G1],C[G1/G2],D[G1]", Paths(back));
        Assert.Equal(["G2"], back.ChildGroups("G1"));
        Assert.Equal(["G1"], back.ParentPath("G2"));
        Assert.True(back.IsAncestorGroup("G1", "G2"));
        Assert.Equal([2, 3, 4], back.Members("G1"));
        Assert.Equal("G1", back.UnitGroup(3, null));   // 外から見ると G1
        Assert.Equal("G2", back.UnitGroup(3, "G1"));   // G1 の中から見ると G2
        Assert.Null(back.UnitGroup(2, "G1"));          // G1 の直下のオブジェクト

        // 内と外が同じオブジェクトで始まる・終わる
        var both = PanelDocument.Parse(Plain);
        both.MakeGroup([2, 3], "外");
        both.MakeGroup([2, 3], "内"); // 外のオブジェクトを全部選んだので、外ごと「内」に入る（外を新しいグループで囲む）
        Assert.Equal("A,B[内/外],C[内/外],D", Paths(both));
        var t2 = both.Serialize();
        Assert.Contains(";===== グループ: 内 =====\r\n;===== グループ: 外 =====\r\n;B", t2);
        Assert.Contains("Location = 30, 30\r\n;===== グループ終わり: 外 =====\r\n;===== グループ終わり: 内 =====\r\n\r\n;D", t2);
        Assert.Equal(t2, PanelDocument.Parse(t2).Serialize());
        Assert.Equal("A,B[内/外],C[内/外],D", Paths(PanelDocument.Parse(t2)));
    }

    [Fact]
    public void MakeGroupNestsInsideCommonGroup()
    {
        var doc = PanelDocument.Parse(Plain);
        doc.MakeGroup([1, 2, 3, 4], "外");
        // 外の中の一部（B・D。間の C は後ろへ）→ 外の中に作る
        Assert.Equal(["外"], doc.CommonParent([2, 4]));
        var idx = doc.MakeGroup([2, 4], "中");
        Assert.Equal([2, 3], idx);
        Assert.Equal("A[外],B[外/中],D[外/中],C[外]", Paths(doc));
        // グループの一部と外のオブジェクトを選ぶ → グループから一部が抜けるので知らせる
        var doc2 = PanelDocument.Parse(Plain);
        doc2.MakeGroup([2, 3], "G");
        Assert.Equal(["G"], doc2.GroupsSplitBy([3, 4]));
        doc2.MakeGroup([3, 4], "H");
        Assert.Equal("A,B[G],C[H],D[H]", Paths(doc2));
        Assert.Equal(doc2.Serialize(), PanelDocument.Parse(doc2.Serialize()).Serialize());
        // グループを丸ごと選び、その外のオブジェクトと一緒にグループにする → グループごと入る
        var doc3 = PanelDocument.Parse(Plain);
        doc3.MakeGroup([2, 3], "G");
        doc3.MakeGroup([1, 2, 3], "W");
        Assert.Equal("A[W],B[W/G],C[W/G],D", Paths(doc3));
    }

    [Fact]
    public void NestedEditsKeepGroupsContiguous()
    {
        var doc = PanelDocument.Parse(Plain);
        doc.MakeGroup([1, 2, 3], "外");
        doc.MakeGroup([2, 3], "中");
        Assert.Equal("A[外],B[外/中],C[外/中],D", Paths(doc));

        // 中のオブジェクトは中の中だけで動く
        Assert.Equal(3, doc.MoveSection(2, 1));
        Assert.Equal("A[外],C[外/中],B[外/中],D", Paths(doc));
        Assert.Equal(3, doc.MoveSection(3, 1));  // 中の最後からは出ない
        // 外の直下の A は、中を丸ごと飛び越える
        Assert.Equal(3, doc.MoveSection(1, 1));
        Assert.Equal("C[外/中],B[外/中],A[外],D", Paths(doc));
        // 中のグループは外の中だけで動く
        Assert.Equal(2, doc.MoveGroup("中", 1));
        Assert.Equal("A[外],C[外/中],B[外/中],D", Paths(doc));
        Assert.Equal(2, doc.MoveGroup("中", 1));   // 外の最後からは出ない
        // 外は丸ごと D と入れ替わる
        Assert.Equal(2, doc.MoveGroup("外", 1));
        Assert.Equal("D,A[外],C[外/中],B[外/中]", Paths(doc));

        // 外したり解除したりしても、残りは続いたまま
        int b = doc.RemoveFromGroup(4);
        Assert.Equal("D,A[外],C[外/中],B[外]", Paths(doc));
        Assert.Equal(4, b);
        doc.Ungroup("外");
        Assert.Equal("D,A,C[中],B", Paths(doc));
        Assert.Equal(doc.Serialize(), PanelDocument.Parse(doc.Serialize()).Serialize());
    }

    [Fact]
    public void DuplicateNestedGroupRenamesInnerGroups()
    {
        var doc = PanelDocument.Parse(Plain);
        doc.MakeGroup([2, 3], "外");
        doc.MakeGroup([3], "中");
        var (copies, name) = doc.DuplicateGroup("外", "外 のコピー", 0, 0);
        Assert.Equal("外 のコピー", name);
        Assert.Equal([4, 5], copies);
        Assert.Equal("A,B[外],C[外/中],B[外 のコピー],C[外 のコピー/中 のコピー],D", Paths(doc));
        var back = PanelDocument.Parse(doc.Serialize());
        Assert.Empty(back.GroupWarnings);
        Assert.Equal(Paths(doc), Paths(back));
        // 名前を変えると、中のオブジェクトの外側の名前も変わる
        back.RenameGroup("外", "枠");
        Assert.Equal("A,B[枠],C[枠/中],B[外 のコピー],C[外 のコピー/中 のコピー],D", Paths(back));
        Assert.Equal(2, back.DeleteGroup("外 のコピー"));
        Assert.Equal("A,B[枠],C[枠/中],D", Paths(back));
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
        var copies = doc.DuplicateGroup("モニタ", "モニタ のコピー", 10, 5).Indices;
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
        back.Sections[3].SetTitle(""); // 名前のないオブジェクト
        back.MakeGroup([3], "H");
        var again = PanelDocument.Parse(back.Serialize());
        Assert.Equal("", again.Sections[3].Title); // 開始行は名前として読まない
    }
}
