namespace BvePanelEditor.Core;

/// <summary>
/// 計器のグループ。グループの計器はファイル内で必ず連続して並ぶようにする
/// （開始行・終了行でかたまりを囲んで書くため）。グループの中にグループは作らない。
/// </summary>
public sealed partial class PanelDocument
{
    /// <summary>グループ名（ファイル内の順）。</summary>
    public List<string> GroupNames() =>
        Sections.Where(s => s.Group != null).Select(s => s.Group!).Distinct().ToList();

    /// <summary>グループの計器の番号（ファイル内の順）。</summary>
    public List<int> Members(string name)
    {
        var list = new List<int>();
        for (int i = 0; i < Sections.Count; i++)
            if (Sections[i].Group == name) list.Add(i);
        return list;
    }

    /// <summary>グループのかたまりの最初と最後の番号。グループがなければ null。</summary>
    public (int First, int Last)? RunOf(string name)
    {
        var m = Members(name);
        return m.Count == 0 ? null : (m[0], m[^1]);
    }

    /// <summary>ほかと重ならないグループ名（重なれば「名前 2」「名前 3」…）。</summary>
    public string UniqueGroupName(string baseName, string? except = null)
    {
        baseName = baseName.Replace("\r", " ").Replace("\n", " ").Trim();
        if (baseName.Length == 0) baseName = "グループ";
        var used = GroupNames().Where(n => n != except).ToHashSet();
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
    /// 計器をグループにする。ばらばらに並んでいれば最初の計器の位置にまとめる（計器どうしの前後は保つ）。
    /// 名前は重ならないように直す。戻り値はグループにした計器の新しい番号。
    /// </summary>
    public List<int> MakeGroup(IEnumerable<int> indices, string name)
    {
        var m = indices.Distinct().Where(i => i >= 0 && i < Sections.Count).Order().ToList();
        if (m.Count == 0) return [];
        name = UniqueGroupName(name);
        var picked = m.Select(i => Sections[i]).ToList();
        for (int k = m.Count - 1; k >= 0; k--) Sections.RemoveAt(m[k]);
        int at = m[0];
        // 別のグループのかたまりの途中に入ってしまうなら、そのかたまりの後ろへずらす
        if (at > 0 && at < Sections.Count && Sections[at - 1].Group is { } g && Sections[at].Group == g)
        {
            while (at < Sections.Count && Sections[at].Group == g) at++;
        }
        foreach (var s in picked) s.Group = name;
        Sections.InsertRange(at, picked);
        return Enumerable.Range(at, picked.Count).ToList();
    }

    /// <summary>グループを解除する（計器はそのまま残す）。</summary>
    public void Ungroup(string name)
    {
        foreach (var s in Sections.Where(s => s.Group == name)) s.Group = null;
    }

    /// <summary>グループ名を変える。重なれば番号を付ける。戻り値は実際の名前。</summary>
    public string RenameGroup(string oldName, string newName)
    {
        var n = UniqueGroupName(newName, except: oldName);
        foreach (var s in Sections.Where(s => s.Group == oldName)) s.Group = n;
        return n;
    }

    /// <summary>
    /// 1 つの計器をグループから外す。かたまりの途中にあれば、かたまりのすぐ後ろへ移す。戻り値は新しい番号。
    /// </summary>
    public int RemoveFromGroup(int index)
    {
        var s = Sections[index];
        if (s.Group is not { } g) return index;
        var (first, last) = RunOf(g)!.Value;
        s.Group = null;
        if (index == first || index == last) return index;
        Sections.RemoveAt(index);
        Sections.Insert(last, s);
        return last;
    }

    /// <summary>
    /// 1 つの計器を前後に動かす。グループの中の計器はグループの中だけで動き、
    /// グループの外の計器は隣のグループを丸ごと飛び越える。戻り値は新しい番号。
    /// </summary>
    public int MoveSection(int index, int delta)
    {
        int n = index + Math.Sign(delta);
        if (n < 0 || n >= Sections.Count) return index;
        var s = Sections[index];
        if (s.Group != null)
        {
            if (Sections[n].Group != s.Group) return index;
            (Sections[index], Sections[n]) = (Sections[n], Sections[index]);
            return n;
        }
        if (Sections[n].Group is { } other)
        {
            var (first, last) = RunOf(other)!.Value;
            Sections.RemoveAt(index);
            if (delta > 0)
            {
                Sections.Insert(last, s); // 取り除いたぶん 1 つ前にずれているので last がかたまりの直後
                return last;
            }
            Sections.Insert(first, s);
            return first;
        }
        (Sections[index], Sections[n]) = (Sections[n], Sections[index]);
        return n;
    }

    /// <summary>グループのかたまりを前後に動かす（隣が別のグループならそれを丸ごと飛び越える）。戻り値は新しい最初の番号。</summary>
    public int MoveGroup(string name, int delta)
    {
        if (RunOf(name) is not { } run) return -1;
        var (first, last) = run;
        int n = delta > 0 ? last + 1 : first - 1;
        if (n < 0 || n >= Sections.Count) return first;
        int nFirst = n, nLast = n;
        if (Sections[n].Group is { } other && RunOf(other) is { } orun) (nFirst, nLast) = orun;
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

    /// <summary>グループを複製してかたまりのすぐ後ろに置く。位置は dx, dy ずらす。戻り値は複製の番号。</summary>
    public List<int> DuplicateGroup(string name, string newName, double dx, double dy)
    {
        if (RunOf(name) is not { } run) return [];
        newName = UniqueGroupName(newName);
        var copies = new List<PanelSection>();
        for (int i = run.First; i <= run.Last; i++)
        {
            var c = Sections[i].Clone();
            c.Group = newName;
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
        return Enumerable.Range(run.Last + 1, copies.Count).ToList();
    }

    /// <summary>グループの計器を全部消す。戻り値は消した数。</summary>
    public int DeleteGroup(string name)
    {
        int n = Sections.RemoveAll(s => s.Group == name);
        return n;
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
