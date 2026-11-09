namespace BvePanelEditor.Core;

/// <summary>
/// 計器のグループ。グループの中にグループを入れられる（入れ子）。計器はそれぞれ、外側から順に入っているグループの名前を持つ（PanelSection.Groups）。
/// グループの計器はファイル内で必ず連続して並ぶようにする（開始行・終了行でかたまりを囲んで書くため）。
/// グループ名はファイル全体で重ならないようにする（名前でグループを見分ける）。
/// </summary>
public sealed partial class PanelDocument
{
    /// <summary>グループ名（ファイル内の順。外側のグループが先）。</summary>
    public List<string> GroupNames() =>
        Sections.SelectMany(s => s.Groups).Distinct().ToList();

    /// <summary>グループの計器の番号（中のグループの計器も含む。ファイル内の順）。</summary>
    public List<int> Members(string name)
    {
        var list = new List<int>();
        for (int i = 0; i < Sections.Count; i++)
            if (Sections[i].Groups.Contains(name)) list.Add(i);
        return list;
    }

    /// <summary>グループのかたまりの最初と最後の番号。グループがなければ null。</summary>
    public (int First, int Last)? RunOf(string name)
    {
        var m = Members(name);
        return m.Count == 0 ? null : (m[0], m[^1]);
    }

    /// <summary>グループの外側のグループ（外から順）。いちばん外のグループなら空。</summary>
    public List<string> ParentPath(string name)
    {
        var s = Sections.FirstOrDefault(x => x.Groups.Contains(name));
        return s == null ? [] : s.Groups.Take(s.Groups.IndexOf(name)).ToList();
    }

    /// <summary>グループのすぐ外側のグループ（なければ null）。</summary>
    public string? ParentGroup(string name) => ParentPath(name).LastOrDefault();

    /// <summary>グループの深さ（いちばん外が 0）。</summary>
    public int GroupDepth(string name) => ParentPath(name).Count;

    /// <summary>グループのすぐ中のグループ（ファイル内の順）。</summary>
    public List<string> ChildGroups(string name)
    {
        var list = new List<string>();
        foreach (var s in Sections)
        {
            int k = s.Groups.IndexOf(name);
            if (k >= 0 && k + 1 < s.Groups.Count && !list.Contains(s.Groups[k + 1])) list.Add(s.Groups[k + 1]);
        }
        return list;
    }

    /// <summary>a が b の外側のグループ（b を含む）なら true。同じグループは false。</summary>
    public bool IsAncestorGroup(string a, string b) => ParentPath(b).Contains(a);

    /// <summary>
    /// 計器 index を、外側のグループ parent の中で 1 つの「かたまり」として見たときのグループ。
    /// parent のすぐ中のグループに入っていればそのグループ、parent の直下の計器なら null。
    /// parent が null なら、いちばん外のグループ（入っていなければ null）。
    /// </summary>
    public string? UnitGroup(int index, string? parent)
    {
        var g = Sections[index].Groups;
        int level = parent == null ? 0 : g.IndexOf(parent) + 1; // parent の外の計器は、いちばん外で見る
        return level < g.Count ? g[level] : null;
    }

    /// <summary>ほかと重ならないグループ名（重なれば「名前 2」「名前 3」…）。</summary>
    public string UniqueGroupName(string baseName, string? except = null, ISet<string>? alsoUsed = null)
    {
        baseName = baseName.Replace("\r", " ").Replace("\n", " ").Trim();
        if (baseName.Length == 0) baseName = "グループ";
        var used = GroupNames().Where(n => n != except).ToHashSet();
        if (alsoUsed != null) used.UnionWith(alsoUsed);
        if (!used.Contains(baseName)) return baseName;
        for (int n = 2; ; n++)
            if (!used.Contains($"{baseName} {n}")) return $"{baseName} {n}";
    }

    /// <summary>次の「グループ 1」「グループ 2」…。</summary>
    public string NextGroupName()
    {
        var used = GroupNames().ToHashSet();
        for (int n = 1; ; n++)
            if (!used.Contains($"グループ {n}")) return $"グループ {n}";
    }

    /// <summary>グループにすると並び順が変わる（グループの計器の間にあって、後ろへ回る）計器。</summary>
    public List<int> SectionsReorderedBy(IEnumerable<int> indices)
    {
        var m = indices.Distinct().Order().ToList();
        if (m.Count < 2) return [];
        var set = m.ToHashSet();
        var list = new List<int>();
        for (int i = m[0]; i <= m[^1]; i++)
            if (!set.Contains(i)) list.Add(i);
        return list;
    }

    /// <summary>
    /// 選んだ計器をグループにするときに入れる外側のグループ（外から順）。選んだ計器がみな入っているグループのうち、
    /// 丸ごと選んでいないもの（丸ごと選んでいれば、そのグループごと新しいグループに入れる）。
    /// </summary>
    public List<string> CommonParent(IEnumerable<int> indices)
    {
        var m = indices.Distinct().Where(i => i >= 0 && i < Sections.Count).ToList();
        if (m.Count == 0) return [];
        var sel = m.ToHashSet();
        var p = Sections[m[0]].Groups.ToList();
        foreach (var i in m.Skip(1))
        {
            var g = Sections[i].Groups;
            int k = 0;
            while (k < p.Count && k < g.Count && p[k] == g[k]) k++;
            p.RemoveRange(k, p.Count - k);
        }
        while (p.Count > 0 && Members(p[^1]).All(sel.Contains)) p.RemoveAt(p.Count - 1);
        return p;
    }

    /// <summary>グループにすると、一部の計器だけが抜けるグループ（選んだ計器がそのグループの一部だけのとき）。</summary>
    public List<string> GroupsSplitBy(IEnumerable<int> indices)
    {
        var m = indices.Distinct().Where(i => i >= 0 && i < Sections.Count).ToList();
        var sel = m.ToHashSet();
        var parent = CommonParent(m);
        return m.SelectMany(i => Sections[i].Groups).Distinct()
            .Where(g => !parent.Contains(g) && !Members(g).All(sel.Contains)).ToList();
    }

    /// <summary>
    /// 計器をグループにする。選んだ計器がみな同じグループの中なら、そのグループの中に作る（入れ子）。
    /// 丸ごと選んだグループは、グループごと新しいグループに入れる。一部だけ選んだグループからは、その計器を外す。
    /// ばらばらに並んでいれば最初の計器の位置にまとめる（計器どうしの前後は保つ）。
    /// 名前は重ならないように直す。戻り値はグループにした計器の新しい番号。
    /// </summary>
    public List<int> MakeGroup(IEnumerable<int> indices, string name)
    {
        var m = indices.Distinct().Where(i => i >= 0 && i < Sections.Count).Order().ToList();
        if (m.Count == 0) return [];
        name = UniqueGroupName(name);
        var sel = m.ToHashSet();
        var parent = CommonParent(m);
        // 新しい並び（外側 → 新しいグループ → 丸ごと選んだ中のグループ）を先に決める（Members は並べ替える前に調べる）
        var paths = new List<List<string>>();
        foreach (var i in m)
        {
            var rest = Sections[i].Groups.Skip(parent.Count).ToList();
            int keep = 0;
            while (keep < rest.Count && Members(rest[keep]).All(sel.Contains)) keep++;
            paths.Add([.. parent, name, .. rest.Take(keep)]);
        }
        var picked = m.Select(i => Sections[i]).ToList();
        for (int k = m.Count - 1; k >= 0; k--) Sections.RemoveAt(m[k]);
        int at = m[0];
        // ほかのグループのかたまりの途中に入ってしまうなら、そのかたまりの後ろへずらす
        while (at > 0 && at < Sections.Count)
        {
            var a = Sections[at - 1].Groups;
            var b = Sections[at].Groups;
            int c = 0;
            while (c < a.Count && c < b.Count && a[c] == b[c]) c++;
            if (c <= parent.Count) break;
            at = RunOf(a[parent.Count])!.Value.Last + 1;
        }
        for (int k = 0; k < picked.Count; k++)
        {
            picked[k].Groups.Clear();
            picked[k].Groups.AddRange(paths[k]);
        }
        Sections.InsertRange(at, picked);
        return Enumerable.Range(at, picked.Count).ToList();
    }

    /// <summary>グループを解除する（計器と、中のグループはそのまま残す）。</summary>
    public void Ungroup(string name)
    {
        foreach (var s in Sections) s.Groups.Remove(name);
    }

    /// <summary>グループ名を変える。重なれば番号を付ける。戻り値は実際の名前。</summary>
    public string RenameGroup(string oldName, string newName)
    {
        var n = UniqueGroupName(newName, except: oldName);
        foreach (var s in Sections)
        {
            int k = s.Groups.IndexOf(oldName);
            if (k >= 0) s.Groups[k] = n;
        }
        return n;
    }

    /// <summary>
    /// 1 つの計器を、いちばん内側のグループから外す（外側のグループには残る）。かたまりの途中にあれば、かたまりのすぐ後ろへ移す。
    /// 戻り値は新しい番号。
    /// </summary>
    public int RemoveFromGroup(int index)
    {
        var s = Sections[index];
        if (s.Group is not { } g) return index;
        var (first, last) = RunOf(g)!.Value;
        s.Groups.RemoveAt(s.Groups.Count - 1);
        if (index == first || index == last) return index;
        Sections.RemoveAt(index);
        Sections.Insert(last, s);
        return last;
    }

    /// <summary>
    /// 1 つの計器を前後に動かす。入っているグループの中だけで動き、隣のグループ（同じ高さのグループ）は丸ごと飛び越える。
    /// 戻り値は新しい番号。
    /// </summary>
    public int MoveSection(int index, int delta) => MoveRun(index, index, Sections[index].Groups.ToList(), delta);

    /// <summary>
    /// グループのかたまりを前後に動かす。外側のグループの中だけで動き、隣のグループは丸ごと飛び越える。戻り値は新しい最初の番号。
    /// </summary>
    public int MoveGroup(string name, int delta)
    {
        if (RunOf(name) is not { } run) return -1;
        return MoveRun(run.First, run.Last, ParentPath(name), delta);
    }

    private static bool StartsWith(List<string> path, List<string> prefix) =>
        path.Count >= prefix.Count && prefix.Select((p, k) => path[k] == p).All(x => x);

    /// <summary>first〜last のかたまりを、外側のグループ parent の中で隣のかたまりと入れ替える。戻り値は新しい最初の番号。</summary>
    private int MoveRun(int first, int last, List<string> parent, int delta)
    {
        int n = delta > 0 ? last + 1 : first - 1;
        if (n < 0 || n >= Sections.Count || !StartsWith(Sections[n].Groups, parent)) return first;
        int nFirst = n, nLast = n;
        if (Sections[n].Groups.Count > parent.Count && RunOf(Sections[n].Groups[parent.Count]) is { } orun) (nFirst, nLast) = orun;
        var neighbor = Sections.GetRange(nFirst, nLast - nFirst + 1);
        Sections.RemoveRange(nFirst, neighbor.Count);
        if (delta > 0)
        {
            Sections.InsertRange(first, neighbor);
            return first + neighbor.Count;
        }
        int count = last - first + 1;
        Sections.InsertRange(nFirst + count, neighbor);
        return nFirst;
    }

    /// <summary>
    /// グループを複製してかたまりのすぐ後ろに置く（中のグループも、名前に「のコピー」を付けて複製する）。位置は dx, dy ずらす。
    /// 戻り値は複製の番号と、複製したグループの名前。
    /// </summary>
    public (List<int> Indices, string Name) DuplicateGroup(string name, string newName, double dx, double dy)
    {
        if (RunOf(name) is not { } run) return ([], "");
        // 複製するグループ（name と、その中のグループ）の新しい名前
        var used = new HashSet<string>();
        var map = new Dictionary<string, string>();
        newName = UniqueGroupName(newName, alsoUsed: used);
        used.Add(newName);
        map[name] = newName;
        for (int i = run.First; i <= run.Last; i++)
        {
            var g = Sections[i].Groups;
            for (int k = g.IndexOf(name) + 1; k < g.Count; k++)
            {
                if (map.ContainsKey(g[k])) continue;
                var n = UniqueGroupName($"{g[k]} のコピー", alsoUsed: used);
                used.Add(n);
                map[g[k]] = n;
            }
        }
        var copies = new List<PanelSection>();
        for (int i = run.First; i <= run.Last; i++)
        {
            var c = Sections[i].Clone();
            for (int k = 0; k < c.Groups.Count; k++) if (map.TryGetValue(c.Groups[k], out var to)) c.Groups[k] = to;
            c.PsdLayers.Clear();
            c.CropSet = null;
            c.CropShapes.Clear();
            c.IsMainPanel = false;
            if (PanelValues.TryPoint(c.Get("Location"), out var x, out var y)) c.Set("Location", PanelValues.Point(x + dx, y + dy));
            copies.Add(c);
        }
        if (copies.Count > 0 && (copies[0].Leading.Count == 0 || copies[0].Leading[0].Kind != PanelLineKind.Blank))
            copies[0].Leading.Insert(0, PanelLine.Blank());
        Sections.InsertRange(run.Last + 1, copies);
        return (Enumerable.Range(run.Last + 1, copies.Count).ToList(), newName);
    }

    /// <summary>グループの計器を全部消す（中のグループも）。戻り値は消した数。</summary>
    public int DeleteGroup(string name) => Sections.RemoveAll(s => s.Groups.Contains(name));

    /// <summary>
    /// 外側のグループ parent の中の「かたまり」（すぐ中のグループは丸ごと、直下の計器は 1 つずつ）。indices の中だけで作る。
    /// parent が null なら、いちばん外で見る。
    /// </summary>
    public List<List<int>> UnitsWithin(IEnumerable<int> indices, string? parent)
    {
        var units = new List<List<int>>();
        var byGroup = new Dictionary<string, List<int>>();
        foreach (var i in indices.Distinct().Order())
        {
            if (UnitGroup(i, parent) is { } g)
            {
                if (!byGroup.TryGetValue(g, out var u)) { u = []; byGroup[g] = u; units.Add(u); }
                u.Add(i);
            }
            else units.Add([i]);
        }
        return units;
    }

    /// <summary>計器の位置（Location）をまとめて動かす。This など位置のないものは飛ばす。</summary>
    public void Offset(IEnumerable<int> indices, double dx, double dy)
    {
        foreach (var i in indices.Distinct())
        {
            var s = Sections[i];
            if (PanelValues.TryPoint(s.Get("Location"), out var x, out var y))
                s.Set("Location", PanelValues.Point(x + dx, y + dy));
        }
    }
}
