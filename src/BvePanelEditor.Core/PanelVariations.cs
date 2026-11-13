using System.Text.RegularExpressions;

namespace BvePanelEditor.Core;

/// <summary>
/// バリエーション（路線ごとなどに作り分けるパネル）1 つ。マスターパネルの前置きに「;@バリエーション: 名前 = 出力ファイル」と書く。
/// </summary>
public sealed class PanelVariation
{
    public string Name { get; set; } = "";
    /// <summary>書き出すパネルファイル（マスターのフォルダからの相対パス、またはフルパス）。</summary>
    public string File { get; set; } = "";
}

/// <summary>
/// オブジェクトが入るバリエーションの条件（ファイルでは「; @group 条件」）。書き方:
/// 「,」か「、」で区切り、どれかが合えば入る。「A」はバリエーションが A のとき、「!A」は A 以外のとき。空・「共通」は共通。
/// </summary>
public static partial class VariationCondition
{
    /// <summary>どのバリエーションにも入らないオブジェクトの条件（一覧に使えない名前）。</summary>
    public const string None = "なし";

    [GeneratedRegex(@"[\s,!;#\[\]=、]")]
    private static partial Regex BadChars();

    /// <summary>バリエーション名に使えるか（空白・, ! ; # [ ] = 、 と「なし」「共通」は使えない）。</summary>
    public static bool IsValidName(string name) =>
        name.Length > 0 && !BadChars().IsMatch(name) && name != None && name != "共通" && name != "(共通)";

    /// <summary>条件を項に分ける。共通なら null。</summary>
    public static List<(bool Neg, string Name)>? Parse(string? text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0 || text is "共通" or "(共通)") return null;
        var terms = new List<(bool, string)>();
        foreach (var raw in text.Split(',', '、'))
        {
            var part = raw.Trim();
            if (part.Length == 0) continue;
            bool neg = part.StartsWith('!');
            var name = neg ? part[1..].Trim() : part;
            if (name.Length == 0) continue;
            if (!terms.Contains((neg, name))) terms.Add((neg, name));
        }
        return terms.Count == 0 ? null : terms;
    }

    /// <summary>項を条件の文字列にする。項がなければ null（共通）。</summary>
    public static string? Format(IEnumerable<(bool Neg, string Name)> terms)
    {
        var list = terms.Select(t => (t.Neg ? "!" : "") + t.Name).ToList();
        return list.Count == 0 ? null : string.Join(", ", list);
    }

    /// <summary>バリエーション variation に入るか。</summary>
    public static bool Match(string? cond, string variation) =>
        Parse(cond) is not { } terms || terms.Any(t => t.Neg ? t.Name != variation : t.Name == variation);

    /// <summary>条件の中の名前（「!」の付いたものも）。</summary>
    public static IEnumerable<string> Names(string? cond) => Parse(cond)?.Select(t => t.Name) ?? [];

    /// <summary>入るバリエーションの集まりから条件を作る（全部なら null、どれにも入らなければ「なし」）。</summary>
    public static string? FromSet(IReadOnlyList<string> all, IEnumerable<string> members)
    {
        var set = members.ToHashSet();
        var list = all.Where(set.Contains).ToList();
        if (all.Count > 0 && list.Count == all.Count) return null;
        return list.Count == 0 ? None : string.Join(", ", list);
    }
}

/// <summary>バリエーションの一覧・オブジェクトの入るバリエーション・書き出し。</summary>
public sealed partial class PanelDocument
{
    /// <summary>バリエーションの一覧（ファイルの前置きの「;@バリエーション」の行。並びも一覧の順）。</summary>
    public List<PanelVariation> Variations { get; } = [];

    /// <summary>RealAnalogGauge の親 ini（パネルファイルからの相対パス。手で選んだときだけ書く）。ファイルでは「;@RealAnalogGauge: パス」。</summary>
    public string? RagIni { get; set; }

    public List<string> VariationNames => Variations.Select(v => v.Name).ToList();

    /// <summary>オブジェクトが入るバリエーション（一覧の順）。</summary>
    public List<string> MembershipOf(int index) => Variations.Select(v => v.Name).Where(n => Sections[index].InVariation(n)).ToList();

    /// <summary>
    /// オブジェクトをバリエーション name に入れる・外す。条件は入るバリエーションの並び（全部なら共通、どれにも入らなければ「なし」）に作り直す。
    /// 一覧にない名前の項（「!」の付かないもの）はそのまま残す。
    /// </summary>
    public void SetMembership(IEnumerable<int> indices, string name, bool include)
    {
        var all = VariationNames;
        foreach (var i in indices.Distinct())
        {
            var s = Sections[i];
            var members = MembershipOf(i);
            if (include && !members.Contains(name)) members.Add(name);
            if (!include) members.Remove(name);
            var unknown = VariationCondition.Parse(s.VariationCond)?.Where(t => !t.Neg && !all.Contains(t.Name) && t.Name != VariationCondition.None).Select(t => t.Name).ToList() ?? [];
            var cond = VariationCondition.FromSet(all, members);
            // 一覧にない名前は、一部のバリエーションだけに入るときに残す（全部に入る＝共通なら要らない）
            if (unknown.Count > 0 && cond != null) cond = string.Join(", ", (cond == VariationCondition.None ? [] : cond.Split(", ")).Concat(unknown));
            s.VariationCond = cond;
        }
    }

    /// <summary>バリエーション名を変える（オブジェクトの条件の中の名前も）。</summary>
    public void RenameVariation(string oldName, string newName)
    {
        foreach (var v in Variations.Where(v => v.Name == oldName)) v.Name = newName;
        foreach (var s in Sections)
        {
            if (VariationCondition.Parse(s.VariationCond) is not { } terms || !terms.Any(t => t.Name == oldName)) continue;
            s.VariationCond = VariationCondition.Format(terms.Select(t => (t.Neg, t.Name == oldName ? newName : t.Name)));
        }
    }

    /// <summary>
    /// バリエーションを一覧から消す。オブジェクトの条件は残りのバリエーションで作り直す。
    /// 戻り値は、そのバリエーションだけに入っていたオブジェクト（どのバリエーションにも入らなくなったオブジェクト）。
    /// </summary>
    public List<int> DeleteVariation(string name)
    {
        var before = Enumerable.Range(0, Sections.Count).Select(MembershipOf).ToList();
        Variations.RemoveAll(v => v.Name == name);
        var all = VariationNames;
        var orphans = new List<int>();
        for (int i = 0; i < Sections.Count; i++)
        {
            if (Sections[i].VariationCond == null) continue;
            var members = before[i].Where(n => n != name).ToList();
            if (members.Count == 0 && before[i].Contains(name)) orphans.Add(i);
            Sections[i].VariationCond = VariationCondition.FromSet(all, members);
        }
        return orphans;
    }

    /// <summary>条件に出てくるのに一覧にないバリエーション名。</summary>
    public List<string> UnknownVariationNames()
    {
        var all = VariationNames.ToHashSet();
        return Sections.SelectMany(s => VariationCondition.Names(s.VariationCond))
            .Where(n => n != VariationCondition.None && !all.Contains(n)).Distinct().ToList();
    }

    /// <summary>
    /// バリエーション name のパネルファイルの中身を作る。入らないオブジェクトを消し、条件の印とバリエーションの一覧の行を書かない。
    /// 出力先のフォルダ outDir がマスターのフォルダ masterDir と違えば、画像の場所（相対パス）を直す。
    /// </summary>
    public string ExportVariation(string name, string? masterDir = null, string? outDir = null)
    {
        var doc = Parse(Serialize());
        doc.Sections.RemoveAll(s => !s.InVariation(name));
        foreach (var s in doc.Sections) s.VariationCond = null;
        doc.Variations.Clear();
        if (masterDir != null && outDir != null && !SameDir(masterDir, outDir))
        {
            foreach (var s in doc.Sections) RebasePaths(s, masterDir, outDir);
            // 切り抜き元・PSD とのつながりの行の場所も
            foreach (var c in doc.CropSets) { c.Day = RebasePath(c.Day, masterDir, outDir); if (c.Night != null) c.Night = RebasePath(c.Night, masterDir, outDir); }
            foreach (var p in doc.PsdSources) { p.Day = RebasePath(p.Day, masterDir, outDir); if (p.Night != null) p.Night = RebasePath(p.Night, masterDir, outDir); }
        }
        return doc.Serialize();
    }

    /// <summary>a と b が同じフォルダか。</summary>
    public static bool SameDir(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// オブジェクトの画像（DaytimeImage など）の相対パスを、fromDir からのものから toDir からのものに直す。フルパスはそのまま。
    /// 区切りは元の書き方（\ か /）に合わせる。
    /// </summary>
    public static void RebasePaths(PanelSection s, string fromDir, string toDir)
    {
        var imageKeys = PanelSpec.For(s.Kind).Where(k => k.Type == KeyType.Image).Select(k => k.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var l in s.Lines)
        {
            if (l.Kind != PanelLineKind.KeyValue || !imageKeys.Contains(l.Key)) continue;
            var v = l.Value.Trim().Trim('"');
            var rel = RebasePath(v, fromDir, toDir);
            if (rel != v) l.SetValue(rel);
        }
    }

    /// <summary>相対パス 1 つを、fromDir からのものから toDir からのものに直す（空・フルパスはそのまま。区切りは元の書き方に合わせる）。</summary>
    public static string RebasePath(string v, string fromDir, string toDir)
    {
        if (v.Length == 0 || Path.IsPathRooted(v)) return v;
        var full = Path.GetFullPath(Path.Combine(fromDir, v.Replace('/', '\\')));
        var rel = Path.GetRelativePath(toDir, full);
        if (v.Contains('/') && !v.Contains('\\')) rel = rel.Replace('\\', '/');
        return rel;
    }

    /// <summary>出力ファイルのフルパス（マスターのフォルダ基準）。</summary>
    public static string VariationOutputPath(string masterPath, PanelVariation v) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(masterPath))!, v.File));
}
