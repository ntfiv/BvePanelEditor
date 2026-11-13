using BvePanelEditor.Core;
using Xunit;

namespace BvePanelEditor.Tests;

public sealed class VariationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "panelkun_var_" + Guid.NewGuid().ToString("N"));
    public VariationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    static string Lamp(string name, string subject, string img = "a.png") =>
        $"\r\n;{name}\r\n[PilotLamp]\r\nSubject = {subject}\r\nLocation = 0, 0\r\nDaytimeImage = {img}\r\n";
    const string Head = "BveTs Instrument Panel 2.00\r\n\r\n[This]\r\nResolution = 1024\r\nDaytimeImage = base.png\r\n";

    static string Titles(PanelDocument d) => string.Join(",", d.Sections.Skip(1).Select(s => s.Title + (s.VariationCond != null ? "{" + s.VariationCond + "}" : "")));

    [Fact]
    public void ConditionMatches()
    {
        Assert.True(VariationCondition.Match(null, "A"));
        Assert.True(VariationCondition.Match("共通", "A"));
        Assert.True(VariationCondition.Match("A, B", "B"));
        Assert.False(VariationCondition.Match("A, B", "C"));
        Assert.True(VariationCondition.Match("!A", "B"));
        Assert.False(VariationCondition.Match("!A", "A"));
        Assert.True(VariationCondition.Match("A、C", "C"));
        Assert.False(VariationCondition.Match(VariationCondition.None, "A"));
        Assert.Equal("A, B", VariationCondition.FromSet(["A", "B", "C"], ["B", "A"]));
        Assert.Null(VariationCondition.FromSet(["A", "B"], ["A", "B"]));
        Assert.Equal(VariationCondition.None, VariationCondition.FromSet(["A", "B"], []));
        Assert.False(VariationCondition.IsValidName("a b"));
        Assert.False(VariationCondition.IsValidName("a,b"));
        Assert.False(VariationCondition.IsValidName(VariationCondition.None));
        Assert.True(VariationCondition.IsValidName("ワンマン_雨"));
    }

    [Fact]
    public void MarkersAndListRoundTrip()
    {
        // 続けて書く @group・!・@end を読んで、そのまま書ける
        var text = "BveTs Instrument Panel 2.00\r\n;@バリエーション: A = gen_a.txt\r\n;@バリエーション: B = ..\\B\\panel.txt\r\n\r\n[This]\r\nResolution = 1024\r\n"
            + "\r\n; @group A\r\n;灯1\r\n[PilotLamp]\r\nSubject = ats1\r\n"
            + "\r\n; @group !A\r\n;灯2\r\n[PilotLamp]\r\nSubject = ats2\r\n"
            + "\r\n; @end\r\n;灯3\r\n[PilotLamp]\r\nSubject = ats3\r\n"
            + "\r\n; @group A, B\r\n;灯4\r\n[PilotLamp]\r\nSubject = ats4\r\n; @end\r\n";
        var doc = PanelDocument.Parse(text);
        Assert.Empty(doc.GroupWarnings);
        Assert.Equal(["A", "B"], doc.VariationNames);
        Assert.Equal("..\\B\\panel.txt", doc.Variations[1].File);
        Assert.Equal("灯1{A},灯2{!A},灯3,灯4{A, B}", Titles(doc));
        Assert.Equal(text, doc.Serialize());
        Assert.True(doc.Sections[2].InVariation("B") && !doc.Sections[2].InVariation("A"));

        // 閉じ忘れ・対応のない @end は知らせる
        Assert.Single(PanelDocument.Parse("BveTs Instrument Panel 2.00\r\n; @group A\r\n[PilotLamp]\r\n").GroupWarnings);
        Assert.Single(PanelDocument.Parse("BveTs Instrument Panel 2.00\r\n[PilotLamp]\r\n; @end\r\n").GroupWarnings);
    }

    [Fact]
    public void MarkersWorkWithLayoutGroups()
    {
        var doc = PanelDocument.Parse(Head + Lamp("A", "ats1") + Lamp("B", "ats2") + Lamp("C", "ats3"));
        doc.MakeGroup([1, 2], "G");
        doc.Variations.Add(new() { Name = "X", File = "x.txt" });
        doc.Variations.Add(new() { Name = "Y", File = "y.txt" });
        doc.SetMembership([2], "Y", false);
        var text = doc.Serialize();
        Assert.Contains("\r\n\r\n; @group X\r\n;B\r\n[PilotLamp]", text);
        Assert.Contains("\r\n; @end\r\n;C", text);
        var back = PanelDocument.Parse(text);
        Assert.Empty(back.GroupWarnings);
        Assert.Equal(text, back.Serialize());
        Assert.Equal("A,B{X},C", Titles(back));
        Assert.Equal(["G"], back.Sections[2].Groups);
    }

    [Fact]
    public void MembershipRenameDeleteExport()
    {
        var doc = PanelDocument.Parse(Head + Lamp("共通", "ats1") + Lamp("ワンマン", "ats2") + Lamp("雨", "ats3", "rain\\drop.png"));
        doc.Variations.Add(new() { Name = "ツーマン", File = "panel_2man.txt" });
        doc.Variations.Add(new() { Name = "ワンマン", File = "panel_1man.txt" });
        doc.Variations.Add(new() { Name = "雨", File = "sub\\panel_rain.txt" });
        doc.SetMembership([2], "ツーマン", false);           // ワンマン・雨
        doc.SetMembership([3], "ツーマン", false);
        doc.SetMembership([3], "ワンマン", false);           // 雨だけ
        Assert.Equal("共通,ワンマン{ワンマン, 雨},雨{雨}", Titles(doc));
        doc.SetMembership([3], "雨", false);
        Assert.Equal("共通,ワンマン{ワンマン, 雨},雨{なし}", Titles(doc));
        doc.SetMembership([3], "雨", true);
        doc.SetMembership([2], "ツーマン", true);
        Assert.Equal("共通,ワンマン,雨{雨}", Titles(doc));
        doc.SetMembership([2], "ツーマン", false);

        // 書き出し: 入らないオブジェクト・印・一覧の行は書かない
        var two = doc.ExportVariation("ツーマン");
        Assert.DoesNotContain("@group", two);
        Assert.DoesNotContain("@バリエーション", two);
        Assert.Equal("共通", string.Join(",", PanelDocument.Parse(two).Sections.Skip(1).Select(s => s.Title)));
        var rain = PanelDocument.Parse(doc.ExportVariation("雨", _dir, Path.Combine(_dir, "sub")));
        Assert.Equal("共通,ワンマン,雨", string.Join(",", rain.Sections.Skip(1).Select(s => s.Title)));
        Assert.Equal("..\\rain\\drop.png", rain.Sections[3].Get("DaytimeImage")); // 別のフォルダへ書き出すと画像の場所を直す
        Assert.Equal("..\\base.png", rain.Sections[0].Get("DaytimeImage"));

        // 名前を変えると条件の中も変わる
        doc.RenameVariation("雨", "雨天");
        Assert.Equal("共通,ワンマン{ワンマン, 雨天},雨{雨天}", Titles(doc));
        // 消すと、そのバリエーションだけのオブジェクトを返す
        var orphans = doc.DeleteVariation("雨天");
        Assert.Equal([3], orphans);
        Assert.Equal("共通,ワンマン{ワンマン},雨{なし}", Titles(doc));
        Assert.Equal(["ツーマン", "ワンマン"], doc.VariationNames);
        Assert.Empty(doc.UnknownVariationNames());
    }

    [Fact]
    public void UnknownNamesDoNotSurviveWhenCommon()
    {
        // 条件に一覧にない名前（X）が混じっていても、一覧の全部に入れたら共通になる（X だけ残ってどれにも入らない、にならない）
        var doc = PanelDocument.Parse(Head + Lamp("灯", "ats1"));
        doc.Variations.Add(new() { Name = "A", File = "a.txt" });
        doc.Variations.Add(new() { Name = "B", File = "b.txt" });
        doc.Sections[1].VariationCond = "A, X";
        doc.SetMembership([1], "B", true);
        Assert.Null(doc.Sections[1].VariationCond);
        // 一部だけなら、一覧にない名前は残す
        doc.Sections[1].VariationCond = "X";
        doc.SetMembership([1], "A", true);
        Assert.Equal("A, X", doc.Sections[1].VariationCond);
    }

    [Fact]
    public void ExportToOtherFolderRebasesCropAndPsdLinks()
    {
        var text = "BveTs Instrument Panel 2.00\r\n;===== 切り抜き元 発光: 昼=lit.png | 夜=lit_N.png =====\r\n;===== PSD 昼: psd\\panel.psd =====\r\n\r\n[This]\r\nResolution = 1024\r\n"
            + "\r\n[PilotLamp]\r\n;@PSDレイヤー: 灯\r\n;@切り抜き: 発光\r\nSubject = ats1\r\nLocation = 0, 0\r\nDaytimeImage = crop\\a.png\r\n";
        var doc = PanelDocument.Parse(text);
        doc.Variations.Add(new() { Name = "A", File = "..\\out\\a.txt" });
        var outText = doc.ExportVariation("A", _dir, Path.Combine(_dir, "..", "out"));
        Assert.Contains("昼=..\\" + Path.GetFileName(_dir) + "\\lit.png", outText);
        Assert.Contains("PSD 昼: ..\\" + Path.GetFileName(_dir) + "\\psd\\panel.psd", outText);
        Assert.Contains("DaytimeImage = ..\\" + Path.GetFileName(_dir) + "\\crop\\a.png", outText);
    }

    [Fact]
    public void MergeInSameFolderKeepsPathSpelling()
    {
        // 同じフォルダのパネルは、画像の場所の書き方（.\ など）を変えない
        var a = Path.Combine(_dir, "a.txt");
        var b = Path.Combine(_dir, "b.txt");
        File.WriteAllText(a, Head + Lamp("灯", "ats1", ".\\x.png"));
        File.WriteAllText(b, Head + Lamp("灯", "ats1", ".\\x.png") + Lamp("雨", "ats2", "img/r.png"));
        var r = VariationMerge.Merge([new("A", a, File.ReadAllText(a)), new("B", b, File.ReadAllText(b))], Path.Combine(_dir, "_m.txt"));
        Assert.Empty(r.Mismatches);
        Assert.Equal(".\\x.png", r.Master.Sections.First(s => s.Title == "灯").Get("DaytimeImage"));
        Assert.Equal("img/r.png", r.Master.Sections.First(s => s.Title == "雨").Get("DaytimeImage"));
        Assert.Equal(File.ReadAllText(b), r.Master.ExportVariation("B", _dir, _dir));
    }

    /// <summary>
    /// BVE_VARIATION_MASTERS（; 区切り）を設定したときだけ動く。「; @group」の印の付いたマスターを読み（読み取りのみ）、
    /// そのまま書くと元と同じになり、印の読み間違いがないことを確かめる。
    /// </summary>
    static string LampAt(string name, string loc) =>
        $"\r\n;{name}\r\n[PilotLamp]\r\nSubject = ats\r\nLocation = {loc}\r\nDaytimeImage = a.png\r\n";

    [Fact]
    public void NearMissValueDifferenceIsReported()
    {
        // 同じ名前・同じキーの顔ぶれで、値だけが違う: 分けたまま、マスターから検出して知らせる（取り込みの知らせには混ぜない）
        var a = Path.Combine(_dir, "a.txt"); var b = Path.Combine(_dir, "b.txt");
        File.WriteAllText(a, Head + LampAt("速度計", "0, 0"));
        File.WriteAllText(b, Head + LampAt("速度計", "10, 0"));
        var r = VariationMerge.Merge([new("A", a, File.ReadAllText(a)), new("B", b, File.ReadAllText(b))], Path.Combine(_dir, "_m.txt"));
        Assert.Equal(2, r.Master.Sections.Count(s => s.Title == "速度計")); // まとめ方は変えない
        Assert.DoesNotContain(r.Notes, n => n.Contains("分かれ"));
        var near = VariationMerge.FindNearMisses(r.Master);
        Assert.Contains(near, n => n.Text.Contains("値の違い") && n.Detail.Contains("Location") && n.Detail.Contains("10, 0"));
    }

    [Fact]
    public void NearMissOrderDifferenceIsReported()
    {
        // 中身は同じで、並びが入れ替わっている: 片方が 2 つに分かれ、マスターから検出できる
        var a = Path.Combine(_dir, "a.txt"); var b = Path.Combine(_dir, "b.txt");
        File.WriteAllText(a, Head + Lamp("X", "sx") + Lamp("Y", "sy"));
        File.WriteAllText(b, Head + Lamp("Y", "sy") + Lamp("X", "sx"));
        var r = VariationMerge.Merge([new("A", a, File.ReadAllText(a)), new("B", b, File.ReadAllText(b))], Path.Combine(_dir, "_m.txt"));
        Assert.Contains(VariationMerge.FindNearMisses(r.Master), n => n.Detail.Contains("並び位置"));
    }

    [Fact]
    public void NearMissNotReportedForOrdinaryPanels()
    {
        // 名前のない別々の表示灯（よくある形）と、同じ表示灯が 2 つある場合では知らせない
        var a = Path.Combine(_dir, "a.txt"); var b = Path.Combine(_dir, "b.txt");
        const string untitled = "\r\n[PilotLamp]\r\nSubject = s1\r\nLocation = 0, 0\r\nDaytimeImage = a.png\r\n"
            + "\r\n[PilotLamp]\r\nSubject = s2\r\nLocation = 9, 9\r\nDaytimeImage = b.png\r\n";
        File.WriteAllText(a, Head + untitled);
        File.WriteAllText(b, Head + untitled);
        var r = VariationMerge.Merge([new("A", a, File.ReadAllText(a)), new("B", b, File.ReadAllText(b))], Path.Combine(_dir, "_m.txt"));
        Assert.Empty(VariationMerge.FindNearMisses(r.Master));
        File.WriteAllText(a, Head + Lamp("同", "s") + Lamp("同", "s"));
        File.WriteAllText(b, Head + Lamp("同", "s") + Lamp("同", "s"));
        r = VariationMerge.Merge([new("A", a, File.ReadAllText(a)), new("B", b, File.ReadAllText(b))], Path.Combine(_dir, "_m.txt"));
        Assert.Empty(VariationMerge.FindNearMisses(r.Master));
        // バリエーションのない普通のパネルでは探さない
        Assert.Empty(VariationMerge.FindNearMisses(PanelDocument.Parse(Head + Lamp("X", "sx"))));
    }

    [Fact]
    public void NearMissNotReportedForDesignedDifferences()
    {
        // 状態量が違うものは、別のオブジェクトとして作られているので知らせない
        var a = Path.Combine(_dir, "a.txt"); var b = Path.Combine(_dir, "b.txt");
        File.WriteAllText(a, Head + "\r\n;速度計\r\n[PilotLamp]\r\nSubject = ats1\r\nLocation = 0, 0\r\nDaytimeImage = a.png\r\n");
        File.WriteAllText(b, Head + "\r\n;速度計\r\n[PilotLamp]\r\nSubject = ats2\r\nLocation = 0, 0\r\nDaytimeImage = a.png\r\n");
        var r = VariationMerge.Merge([new("A", a, File.ReadAllText(a)), new("B", b, File.ReadAllText(b))], Path.Combine(_dir, "_m.txt"));
        Assert.Empty(VariationMerge.FindNearMisses(r.Master));
        // 同じ内容でも、マスターの中で遠くに分かれたものは知らせない（前後のオブジェクトが違うための合わせ込みの都合）
        var text = "BveTs Instrument Panel 2.00\r\n;@バリエーション: A = a.txt\r\n;@バリエーション: B = b.txt\r\n\r\n[This]\r\nResolution = 1024\r\n"
            + "\r\n; @group A" + LampAt("同じ", "0, 0") + "; @end\r\n"
            + Lamp("a1", "s1") + Lamp("a2", "s2") + Lamp("a3", "s3")
            + "\r\n; @group B" + LampAt("同じ", "0, 0") + "; @end\r\n";
        Assert.Empty(VariationMerge.FindNearMisses(PanelDocument.Parse(text)));
    }

    [Fact]
    public void NearMissFoundInHandMadeMaster()
    {
        // 手で書いたマスターでも、条件から入るバリエーションを求めて検出できる
        var text = "BveTs Instrument Panel 2.00\r\n;@バリエーション: A = a.txt\r\n;@バリエーション: B = b.txt\r\n\r\n[This]\r\nResolution = 1024\r\n"
            + "\r\n; @group A" + LampAt("速度計", "0, 0") + "; @end\r\n"
            + "\r\n; @group B" + LampAt("速度計", "10, 0") + "; @end\r\n";
        var near = VariationMerge.FindNearMisses(PanelDocument.Parse(text));
        Assert.Contains(near, n => n.Text.Contains("値の違い") && n.Section > 0);
    }

    [Fact]
    public void RealMastersRoundTrip()
    {
        var list = Environment.GetEnvironmentVariable("BVE_VARIATION_MASTERS");
        if (string.IsNullOrEmpty(list)) return;
        foreach (var f in list.Split(';', StringSplitOptions.RemoveEmptyEntries).Where(File.Exists))
        {
            var (text, _) = BveText.Read(f);
            var doc = PanelDocument.Parse(text);
            Assert.Empty(doc.GroupWarnings);
            Assert.Contains(doc.Sections, s => s.VariationCond != null);
            Assert.Equal(text, doc.Serialize());
        }
    }

    [Fact]
    public void MergeTreatsSpellingDifferencesAsSameObject()
    {
        // 書き方だけの違い（画像の「.\」・Location の空白・キーの順番・数値の書き方）では、同じオブジェクトが割れない
        var a = Path.Combine(_dir, "a.txt");
        var b = Path.Combine(_dir, "b.txt");
        File.WriteAllText(a, Head + Lamp("灯", "ats1", ".\\x.png"));
        File.WriteAllText(b, Head + "\r\n;灯\r\n[PilotLamp]\r\nLocation = 0,0\r\nDaytimeImage = x.png\r\nSubject = ats1\r\n");
        var r = VariationMerge.Merge([new("A", a, File.ReadAllText(a)), new("B", b, File.ReadAllText(b))], Path.Combine(_dir, "_m.txt"));
        Assert.Empty(r.Mismatches);
        Assert.Equal(0, r.Partial); // 割れずに共通として 1 つにまとまる
        Assert.Null(r.Master.Sections.First(s => s.Title == "灯").VariationCond);
        Assert.Contains(r.Notes, n => n.Contains("書き方だけが違う"));
    }

    [Fact]
    public void MergeKeepsTitlesPerVariation()
    {
        // 名前だけ違うオブジェクトはまとめない（まとめると書き出しで名前が元と変わる）
        var a = Path.Combine(_dir, "a.txt");
        var b = Path.Combine(_dir, "b.txt");
        File.WriteAllText(a, Head + Lamp("速度計A", "ats1"));
        File.WriteAllText(b, Head + Lamp("速度計B", "ats1"));
        var r = VariationMerge.Merge([new("A", a, File.ReadAllText(a)), new("B", b, File.ReadAllText(b))], Path.Combine(_dir, "_m.txt"));
        Assert.Empty(r.Mismatches);
        Assert.Equal(File.ReadAllText(a), r.Master.ExportVariation("A", _dir, _dir));
        Assert.Equal(File.ReadAllText(b), r.Master.ExportVariation("B", _dir, _dir));
    }

    [Fact]
    public void MergeNotesCommentOnlyDifference()
    {
        // コメントだけ違うオブジェクトは 1 つにまとめるが、知らせを出す
        var a = Path.Combine(_dir, "a.txt");
        var b = Path.Combine(_dir, "b.txt");
        File.WriteAllText(a, Head + Lamp("灯", "ats1"));
        File.WriteAllText(b, Head + "\r\n;灯\r\n[PilotLamp]\r\nSubject = ats1\r\n;Bだけのコメント\r\nLocation = 0, 0\r\nDaytimeImage = a.png\r\n");
        var r = VariationMerge.Merge([new("A", a, File.ReadAllText(a)), new("B", b, File.ReadAllText(b))], Path.Combine(_dir, "_m.txt"));
        Assert.Null(r.Master.Sections.First(s => s.Title == "灯").VariationCond);
        Assert.Contains(r.Notes, n => n.Contains("コメントが違う"));
    }

    [Fact]
    public void MergeFromExistingPanelsInOtherFolders()
    {
        // 3 つのバリエーション（別々のフォルダ）。共通・一部・1 つだけのオブジェクト、[This] の違い
        string P(string sub, string body)
        {
            var d = Path.Combine(_dir, sub);
            Directory.CreateDirectory(d);
            var p = Path.Combine(d, "panel.txt");
            File.WriteAllText(p, body);
            return p;
        }
        // A・B の [This] は同じ画像（..\img\base.png）、C は表示範囲が違う
        var headA = Head.Replace("base.png", "..\\img\\base.png");
        var head2 = Head.Replace("base.png", "..\\..\\img\\base.png").Replace("Resolution = 1024", "Resolution = 1024\r\nLeft = 10");
        var a = P("A", headA + Lamp("速度", "kmph", "..\\img\\s.png") + Lamp("戸閉", "door", "..\\img\\d.png") + Lamp("ワンマン", "ats200", "..\\img\\w.png"));
        var b = P("B", headA + Lamp("速度", "kmph", "..\\img\\s.png") + Lamp("戸閉", "door", "..\\img\\d.png"));
        var c = P("C\\deep", head2 + Lamp("速度", "kmph", "..\\..\\img\\s.png") + Lamp("雨", "ats150", "..\\..\\img\\r.png") + Lamp("戸閉", "door", "..\\..\\img\\d.png"));
        var sources = new[] { new VariationSource("A", a, File.ReadAllText(a)), new VariationSource("B", b, File.ReadAllText(b)), new VariationSource("C", c, File.ReadAllText(c)) };
        var masterPath = Path.Combine(_dir, "A", "_panel_master.txt");
        var r = VariationMerge.Merge(sources, masterPath);
        var m = r.Master;
        Assert.Empty(r.Mismatches);
        Assert.Equal("速度,雨{C},戸閉,ワンマン{A}", string.Join(",", m.Sections.Where(s => s.Kind != SectionKind.This).Select(s => s.Title + (s.VariationCond != null ? "{" + s.VariationCond + "}" : ""))));
        Assert.Equal(2, m.Sections.Count(s => s.Kind == SectionKind.This)); // [This] が違う
        Assert.Contains(r.Notes, n => n.Contains("[This]"));
        Assert.Equal("..\\img\\r.png", m.Sections.First(s => s.Title == "雨").Get("DaytimeImage")); // マスターの場所からのパスに直す
        Assert.Equal(["A", "B", "C"], m.VariationNames);
        Assert.Equal("panel.txt", m.Variations[0].File);
        Assert.Equal("..\\C\\deep\\panel.txt", m.Variations[2].File);
        // マスターを保存して読み直しても同じに書き出せる
        var text = m.Serialize();
        var back = PanelDocument.Parse(text);
        Assert.Empty(back.GroupWarnings);
        var cOut = PanelDocument.Parse(back.ExportVariation("C", Path.GetDirectoryName(masterPath), Path.GetDirectoryName(c)));
        Assert.Equal(PanelDocument.Parse(File.ReadAllText(c)).Sections.Select(VariationMerge.Key), cOut.Sections.Select(VariationMerge.Key));
        Assert.Equal(File.ReadAllText(b), back.ExportVariation("B", Path.GetDirectoryName(masterPath), Path.GetDirectoryName(b)));
    }
}
