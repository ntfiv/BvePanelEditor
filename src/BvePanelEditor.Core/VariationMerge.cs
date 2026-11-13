namespace BvePanelEditor.Core;

/// <summary>取り込むパネル 1 つ（バリエーション名・パネルファイル・中身）。</summary>
public sealed record VariationSource(string Name, string Path, string Text);

/// <summary>既存のパネルからマスターを作った結果。</summary>
public sealed class VariationMergeResult
{
    public PanelDocument Master { get; init; } = new();
    /// <summary>取り込みで外した・合わせたものの知らせ。</summary>
    public List<string> Notes { get; } = [];
    /// <summary>書き出し直すと元と違うバリエーション（オブジェクト・値・並びが合わない）。空なら全部同じ。</summary>
    public List<string> Mismatches { get; } = [];
    /// <summary>共通のオブジェクトの数・一部のバリエーションだけのオブジェクトの数。</summary>
    public int Common { get; set; }
    public int Partial { get; set; }
}

/// <summary>
/// 既存のバリエーション別パネルから、マスターパネルを作る。
/// 中身（見出しと、キーと値の並び）が同じオブジェクトは 1 つにまとめ、入るバリエーションを条件にする。並びは 1 つ目のパネルを土台に、LCS で合わせる。
/// 画像の相対パスはマスターの場所からのものに直す。
/// </summary>
public static class VariationMerge
{
    /// <summary>
    /// オブジェクトの中身の見分け（見出し・無効かどうか・名前・キーと値。空行は除く）。
    /// 書き方の違いで同じオブジェクトが割れないように、正規化して比べる:
    /// キーの並び順は見ない、値の空白（カンマの前後など）と数値の書き方（1 と 1.0）はそろえる、
    /// 画像の場所は区切り・「.\」・大文字小文字をそろえる、状態量（Subject）は大文字小文字を区別しない。
    /// 名前（タイトル）も中身の一部として含める（名前だけ違うオブジェクトを 1 つにまとめると、書き出しで名前が元と変わってしまうため）。
    /// コメント（名前以外）は含めない代わりに、コメントが違うときは Merge の知らせで伝える。
    /// </summary>
    public static string Key(PanelSection s)
    {
        var imageKeys = PanelSpec.For(s.Kind).Where(k => k.Type == KeyType.Image).Select(k => k.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var subjectKeys = PanelSpec.For(s.Kind).Where(k => k.Type == KeyType.Subject).Select(k => k.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();
        foreach (var l in s.Lines)
        {
            if (l.Kind == PanelLineKind.KeyValue)
                parts.Add($"{l.Key.Trim().ToLowerInvariant()}={NormalizeValue(l.Key.Trim(), l.Value, imageKeys, subjectKeys)}");
            else if (l.Kind == PanelLineKind.Unknown)
                parts.Add("!" + System.Text.RegularExpressions.Regex.Replace(l.Raw.Trim(), @"\s+", " "));
        }
        parts.Sort(StringComparer.Ordinal);
        var head = (s.Disabled ? ";" : "") + s.Name.Trim().ToLowerInvariant();
        return head + "\n#名前:" + s.Title.Trim() + "\n" + string.Join("\n", parts);
    }

    /// <summary>値の正規化（比べるためだけに使う。ファイルに書く値は元のまま）。</summary>
    private static string NormalizeValue(string key, string value, HashSet<string> imageKeys, HashSet<string> subjectKeys)
    {
        var v = value.Trim();
        if (imageKeys.Contains(key))
        {
            v = v.Trim('"').Replace('/', '\\');
            while (v.StartsWith(".\\", StringComparison.Ordinal)) v = v[2..];
            return v.ToLowerInvariant();
        }
        if (subjectKeys.Contains(key) || key.Equals("Subject", StringComparison.OrdinalIgnoreCase)) return v.ToLowerInvariant();
        v = System.Text.RegularExpressions.Regex.Replace(v, @"\s+", " ").Replace(" ,", ",").Replace(", ", ",");
        // 数値だけの要素は書き方をそろえる（「0.0」→「0」、「.5」→「0.5」）。ファイル名など数値でない要素はそのまま
        var toks = v.Split(',');
        for (int i = 0; i < toks.Length; i++)
            if (System.Text.RegularExpressions.Regex.IsMatch(toks[i], @"^[+-]?(\d+\.?\d*|\.\d+)$") &&
                double.TryParse(toks[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                toks[i] = d.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        return string.Join(",", toks);
    }

    /// <summary>コメントだけの署名（比べるため。名前の行も含む）。</summary>
    private static string CommentSig(PanelSection s) =>
        string.Join("\n", s.Leading.Concat(s.Lines).Where(l => l.Kind == PanelLineKind.Comment).Select(l => l.Raw.Trim()));

    /// <summary>正規化しない署名（書き方の違いを見つけるため）。</summary>
    private static string RawSig(PanelSection s)
    {
        var parts = new List<string> { (s.Disabled ? ";" : "") + s.Name.Trim().ToLowerInvariant() };
        foreach (var l in s.Lines)
        {
            if (l.Kind == PanelLineKind.KeyValue) parts.Add($"{l.Key.Trim().ToLowerInvariant()}={l.Value.Trim()}");
            else if (l.Kind == PanelLineKind.Unknown) parts.Add(l.Raw.Trim());
        }
        return string.Join("\n", parts);
    }

    /// <summary>キーごとの値（表示用キー名・元の書き方・比べる用に正規化した値）。重複したキーは最初のもの。</summary>
    private static Dictionary<string, (string Orig, string Raw, string Norm)> ValuesOf(PanelSection s)
    {
        var imageKeys = PanelSpec.For(s.Kind).Where(k => k.Type == KeyType.Image).Select(k => k.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var subjectKeys = PanelSpec.For(s.Kind).Where(k => k.Type == KeyType.Subject).Select(k => k.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var d = new Dictionary<string, (string, string, string)>();
        foreach (var l in s.Lines)
            if (l.Kind == PanelLineKind.KeyValue)
                d.TryAdd(l.Key.Trim().ToLowerInvariant(), (l.Key.Trim(), l.Value.Trim(), NormalizeValue(l.Key.Trim(), l.Value, imageKeys, subjectKeys)));
        return d;
    }

    /// <summary>値比べの組分け（セクションの種類・名前・キーの顔ぶれ・不明な行。値と無効かどうかは含めない）。</summary>
    private static string ShapeKey(PanelSection s)
    {
        var keys = s.Lines.Where(l => l.Kind == PanelLineKind.KeyValue).Select(l => l.Key.Trim().ToLowerInvariant()).Distinct().OrderBy(x => x, StringComparer.Ordinal);
        var unk = s.Lines.Where(l => l.Kind == PanelLineKind.Unknown).Select(l => System.Text.RegularExpressions.Regex.Replace(l.Raw.Trim(), @"\s+", " "));
        return s.Name.Trim().ToLowerInvariant() + "\n#名前:" + s.Title.Trim() + "\n" + string.Join("\n", keys) + "\n" + string.Join("\n", unk);
    }

    /// <summary>知らせでオブジェクトを指す名前（名前があれば「名前 [種類]」、なければ種類と状態量）。</summary>
    private static string Label(PanelSection s)
    {
        if (!string.IsNullOrWhiteSpace(s.Title)) return $"{s.Title.Trim()} [{s.Name.Trim()}]";
        var v = ValuesOf(s);
        var hint = v.TryGetValue("subject", out var sub) ? sub.Raw : v.Values.FirstOrDefault(x => !string.IsNullOrEmpty(x.Raw)).Raw;
        return string.IsNullOrEmpty(hint) ? $"[{s.Name.Trim()}]" : $"[{s.Name.Trim()}]（{hint}）";
    }

    /// <summary>分かれたオブジェクトの知らせ（載せるセクションの位置・短い文・詳しい文）。</summary>
    public sealed record NearMissNote(int Section, string Text, string Detail);

    /// <summary>
    /// マスターの中で、並び位置や値の違いで分かれているオブジェクトを探す。
    /// 各オブジェクトの入るバリエーションは条件（; @group）から求めるので、取り込み直後のマスターでも、手で作ったマスターでも同じように見つかる。
    /// </summary>
    public static List<NearMissNote> FindNearMisses(PanelDocument doc)
    {
        var names = doc.VariationNames;
        if (names.Count < 2) return [];
        var items = doc.Sections.Select((s, i) => (Sec: s, Members: new HashSet<int>(Enumerable.Range(0, names.Count).Where(j => s.InVariation(names[j]))), Pos: i)).ToList();
        return DetectNearMisses(items, names);
    }

    private static List<NearMissNote> DetectNearMisses(IReadOnlyList<(PanelSection Sec, HashSet<int> Members, int Pos)> items, IReadOnlyList<string> names)
    {
        var all = new HashSet<int>(Enumerable.Range(0, names.Count));
        bool Partial(HashSet<int> m) => !m.SetEquals(all);
        string MemberNames(HashSet<int> m) => string.Join("・", names.Where((_, i) => m.Contains(i)));
        var found = new List<NearMissNote>();
        // 内容は同じで、並び位置が違うために分かれたもの
        foreach (var g in items.Where(m => m.Sec.Kind != SectionKind.This).GroupBy(m => Key(m.Sec)))
        {
            var list = g.ToList();
            if (list.Count >= 2 && list.Any(m => Partial(m.Members)) && list.Zip(list.Skip(1)).Any(p => p.Second.Pos - p.First.Pos <= 2))
                found.Add(new NearMissNote(list[0].Pos,
                    $"『{Label(list[0].Sec)}』は内容が同じオブジェクトが {list.Count} つに分かれています",
                    "並び位置の違いで分かれています。元のパネル同士で並びをそろえてから取り込むと 1 つにまとめられます。1 つにまとめると、書き出しの並びはマスターの並びにそろいます"));
        }
        // 名前とキーの顔ぶれが同じで、値が違うために分かれたもの
        foreach (var g in items.Where(m => m.Sec.Kind != SectionKind.This && !string.IsNullOrWhiteSpace(m.Sec.Title)).GroupBy(m => ShapeKey(m.Sec)))
        {
            var list = g.ToList();
            if (list.Count < 2 || !list.Any(m => Partial(m.Members))) continue;
            var reps = list.GroupBy(m => Key(m.Sec)).Select(x => x.First()).ToList(); // 同じ中身同士は並びの話なので 1 つに数える
            if (reps.Count < 2) continue;
            // 状態量（Subject）と画像が違うものは、別のオブジェクトとして作られているので知らせない
            var identity = PanelSpec.For(reps[0].Sec.Kind).Where(k => k.Type is KeyType.Subject or KeyType.Image).Select(k => k.Name.ToLowerInvariant()).ToHashSet();
            var repValues = reps.Select(r => ValuesOf(r.Sec)).ToList();
            if (identity.Any(k => repValues.Select(v => v.TryGetValue(k, out var x) ? x.Norm : null).Distinct().Count() > 1)) continue;
            var (a, b) = (reps[0], reps[1]);
            var diffs = new List<string>();
            if (a.Sec.Disabled != b.Sec.Disabled) diffs.Add("片方は行頭の「;」で無効化されています");
            var av = ValuesOf(a.Sec); var bv = ValuesOf(b.Sec);
            foreach (var k in av.Keys.Union(bv.Keys))
            {
                av.TryGetValue(k, out var x); bv.TryGetValue(k, out var y);
                if (x.Norm == y.Norm) continue;
                diffs.Add($"{(x.Orig ?? y.Orig)}: {MemberNames(a.Members)}={x.Raw ?? "（なし）"} / {MemberNames(b.Members)}={y.Raw ?? "（なし）"}");
                if (diffs.Count >= 4) break;
            }
            if (diffs.Count == 0) continue;
            found.Add(new NearMissNote(a.Pos,
                $"『{Label(a.Sec)}』は値の違いで {list.Count} つに分かれています",
                $"{string.Join("、", diffs)}。1 つにまとめると片方の値で書き出され、見た目が変わる可能性があります。それぞれのオブジェクトを選んで値を確認してください"));
        }
        return found;
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
        bool commentDiff = false;
        bool unifiedDiff = false;

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
                    if (CommentSig(merged[x].Sec) != CommentSig(seq[y])) commentDiff = true;
                    if (RawSig(merged[x].Sec) != RawSig(seq[y])) unifiedDiff = true;
                    next.Add(merged[x]);
                    x++; y++;
                }
                else if (x < a.Count && (y >= b.Count || dp[x + 1, y] >= dp[x, y + 1]))
                {
                    next.Add(merged[x]); // 今までのパネルだけにあるオブジェクト
                    x++;
                }
                else
                {
                    var c = seq[y].Clone(); // このパネルだけにあるオブジェクト
                    next.Add((c, new HashSet<int> { k }));
                    y++;
                }
            }
            merged = next;
        }
        if (commentDiff) notes.Add("バリエーションごとにコメントが違うオブジェクトがありました。マスターでは、先に取り込んだパネルのコメントを使います");
        if (unifiedDiff) notes.Add("書き方だけが違うオブジェクトを 1 つにまとめました（書き出しでは、先に取り込んだパネルの書き方になります）");

        var master = PanelDocument.Parse(sources[0].Text); // 1 行目・前置き・改行・切り抜き元などは 1 つ目のまま
        master.Sections.Clear();
        var names = sources.Select(s => s.Name).ToList();
        int common = 0, partial = 0;
        bool otherMeta = false, otherGroups = false;
        foreach (var (sec, members) in merged)
        {
            sec.VariationCond = members.Count == sources.Count ? null : string.Join(", ", names.Where((_, i) => members.Contains(i)));
            if (sec.VariationCond == null) common++; else partial++;
            // 2 つ目からのパネルだけにあるオブジェクトの、パネルくんの付属情報（グループ・切り抜き元・PSD・メインパネル）は外す
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
        if (otherGroups) notes.Add("2 つ目からのパネルだけにあるオブジェクトのグループは外しました（グループは 1 つ目のパネルのものだけを使います）");
        if (otherMeta) notes.Add("2 つ目からのパネルだけにあるオブジェクトの、切り抜き元・PSD・メインパネルのつながりは外しました");
        // 並べ替えで続かなくなったグループは外す
        foreach (var g in master.GroupNames())
        {
            var m = master.Members(g);
            if (m[^1] - m[0] + 1 == m.Count) continue;
            foreach (var s in master.Sections) s.Groups.Remove(g);
            notes.Add($"グループ「{g}」は、間にほかのバリエーションのオブジェクトが入って続かなくなったので外しました");
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
