namespace BvePanelEditor.Core;

/// <summary>取り込むパネル 1 つ（バリエーション名・パネルファイル・中身）。</summary>
public sealed record VariationSource(string Name, string Path, string Text);

/// <summary>既存のパネルからマスターを作った結果。</summary>
public sealed class VariationMergeResult
{
    public PanelDocument Master { get; init; } = new();
    /// <summary>取り込みで外した・合わせたものの知らせ。</summary>
    public List<string> Notes { get; } = [];
    /// <summary>書き出し直すと元と違うバリエーション（計器・値・並びが合わない）。空なら全部同じ。</summary>
    public List<string> Mismatches { get; } = [];
    /// <summary>共通の計器の数・一部のバリエーションだけの計器の数。</summary>
    public int Common { get; set; }
    public int Partial { get; set; }
}

/// <summary>
/// 既存のバリエーション別パネルから、マスターパネルを作る。
/// 中身（見出しと、キーと値の並び）が同じ計器は 1 つにまとめ、入るバリエーションを条件にする。並びは 1 つ目のパネルを土台に、LCS で合わせる。
/// 画像の相対パスはマスターの場所からのものに直す。
/// </summary>
public static class VariationMerge
{
    /// <summary>計器の中身の見分け（見出し・無効かどうか・キーと値。コメントと空行は除く。キーは大文字小文字を区別しない）。</summary>
    public static string Key(PanelSection s)
    {
        var parts = new List<string> { (s.Disabled ? ";" : "") + s.Name.Trim().ToLowerInvariant() };
        foreach (var l in s.Lines)
        {
            if (l.Kind == PanelLineKind.KeyValue) parts.Add($"{l.Key.Trim().ToLowerInvariant()}={l.Value.Trim()}");
            else if (l.Kind == PanelLineKind.Unknown) parts.Add(l.Raw.Trim());
        }
        return string.Join("\n", parts);
    }

    public static VariationMergeResult Merge(IReadOnlyList<VariationSource> sources, string masterPath)
    {
        if (sources.Count == 0) throw new ArgumentException("取り込むパネルがありません", nameof(sources));
        var masterDir = Path.GetDirectoryName(Path.GetFullPath(masterPath))!;
        var docs = new List<PanelDocument>();
        foreach (var src in sources)
        {
            var d = PanelDocument.Parse(src.Text);
            var dir = Path.GetDirectoryName(Path.GetFullPath(src.Path))!;
            if (!PanelDocument.SameDir(dir, masterDir)) // 同じフォルダなら書き方を変えない
                foreach (var s in d.Sections) PanelDocument.RebasePaths(s, dir, masterDir);
            docs.Add(d);
        }
        var first = docs[0];
        var notes = new List<string>();

        // 1 つ目を土台に、2 つ目から順に並びを合わせる
        var merged = first.Sections.Select(s => (Sec: s, Members: new HashSet<int> { 0 })).ToList();
        for (int k = 1; k < docs.Count; k++)
        {
            var seq = docs[k].Sections;
            var a = merged.Select(m => Key(m.Sec)).ToList();
            var b = seq.Select(Key).ToList();
            // LCS の表
            var dp = new int[a.Count + 1, b.Count + 1];
            for (int i = a.Count - 1; i >= 0; i--)
                for (int j = b.Count - 1; j >= 0; j--)
                    dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            var next = new List<(PanelSection Sec, HashSet<int> Members)>();
            int x = 0, y = 0;
            while (x < a.Count || y < b.Count)
            {
                if (x < a.Count && y < b.Count && a[x] == b[y])
                {
                    merged[x].Members.Add(k);
                    next.Add(merged[x]);
                    x++; y++;
                }
                else if (x < a.Count && (y >= b.Count || dp[x + 1, y] >= dp[x, y + 1]))
                {
                    next.Add(merged[x]); // 今までのパネルだけにある計器
                    x++;
                }
                else
                {
                    var c = seq[y].Clone(); // このパネルだけにある計器
                    next.Add((c, new HashSet<int> { k }));
                    y++;
                }
            }
            merged = next;
        }

        var master = PanelDocument.Parse(sources[0].Text); // 1 行目・前置き・改行・切り抜き元などは 1 つ目のまま
        master.Sections.Clear();
        var names = sources.Select(s => s.Name).ToList();
        int common = 0, partial = 0;
        bool otherMeta = false, otherGroups = false;
        foreach (var (sec, members) in merged)
        {
            sec.VariationCond = members.Count == sources.Count ? null : string.Join(", ", names.Where((_, i) => members.Contains(i)));
            if (sec.VariationCond == null) common++; else partial++;
            // 2 つ目からのパネルだけにある計器の、パネルくんの付属情報（グループ・切り抜き元・PSD・メインパネル）は外す
            if (!members.Contains(0))
            {
                if (sec.Groups.Count > 0) { otherGroups = true; sec.Groups.Clear(); }
                if (sec.CropSet != null || sec.PsdLayers.Count > 0 || sec.IsMainPanel || sec.CropShapes.Count > 0) otherMeta = true;
                sec.CropSet = null;
                sec.PsdLayers.Clear();
                sec.CropShapes.Clear();
                sec.IsMainPanel = false;
            }
            master.Sections.Add(sec);
        }
        if (otherGroups) notes.Add("2 つ目からのパネルだけにある計器のグループは外しました（グループは 1 つ目のパネルのものだけを使います）");
        if (otherMeta) notes.Add("2 つ目からのパネルだけにある計器の、切り抜き元・PSD・メインパネルのつながりは外しました");
        // 並べ替えで続かなくなったグループは外す
        foreach (var g in master.GroupNames())
        {
            var m = master.Members(g);
            if (m[^1] - m[0] + 1 == m.Count) continue;
            foreach (var s in master.Sections) s.Groups.Remove(g);
            notes.Add($"グループ「{g}」は、間にほかのバリエーションの計器が入って続かなくなったので外しました");
        }
        // 切り抜き元・PSD の場所は 1 つ目のパネルのフォルダからのもの。マスターが別のフォルダなら外す
        var firstDir = Path.GetDirectoryName(Path.GetFullPath(sources[0].Path))!;
        if (!string.Equals(firstDir.TrimEnd('\\'), masterDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && (master.CropSets.Count > 0 || master.PsdSources.Count > 0))
        {
            master.CropSets.Clear();
            master.PsdSources.Clear();
            foreach (var s in master.Sections) { s.CropSet = null; s.PsdLayers.Clear(); s.CropShapes.Clear(); }
            notes.Add("マスターを 1 つ目のパネルと別のフォルダに作るので、切り抜き元・PSD とのつながりは外しました");
        }
        if (docs.Skip(1).Any(d => d.HeaderLine.Trim() != first.HeaderLine.Trim()))
            notes.Add($"1 行目（版・文字コード）が違うパネルがあります。マスターは 1 つ目の「{first.HeaderLine.Trim()}」にします");
        int thisCount = master.Sections.Count(s => s.Kind == SectionKind.This);
        if (thisCount > 1) notes.Add($"[This] がバリエーションによって違うので、[This] が {thisCount} 個あります（それぞれ入るバリエーションの条件付き）");

        // バリエーションの一覧（出力ファイルは元のファイル。マスターのフォルダからの相対パス）
        master.Variations.Clear();
        foreach (var src in sources)
            master.Variations.Add(new PanelVariation { Name = src.Name, File = Path.GetRelativePath(masterDir, Path.GetFullPath(src.Path)) });

        var result = new VariationMergeResult { Master = master, Common = common, Partial = partial };
        result.Notes.AddRange(notes);
        // 書き出し直して、元と同じ中身になるかを確かめる
        for (int k = 0; k < sources.Count; k++)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(sources[k].Path))!;
            var back = PanelDocument.Parse(master.ExportVariation(sources[k].Name, masterDir, dir));
            var orig = PanelDocument.Parse(sources[k].Text);
            if (!back.Sections.Select(Key).SequenceEqual(orig.Sections.Select(Key))) result.Mismatches.Add(sources[k].Name);
        }
        return result;
    }
}
