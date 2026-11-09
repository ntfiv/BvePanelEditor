namespace BvePanelEditor.Core.Psd;

/// <summary>レイヤーの役割。</summary>
public enum PsdRole { None, Base, Lamp, Number }

/// <summary>デジタル数字 1 つぶん（コマにするレイヤーを上から順に）。</summary>
public sealed class PsdNumberSpec
{
    public string Name = "";
    public List<string> Frames = [];
}

/// <summary>読み込みの指定。レイヤーは「フォルダ/レイヤー」の形の名前で指す。</summary>
public sealed class PsdImportSpec
{
    public required PsdFile Day;
    public PsdFile? Night;
    /// <summary>パネルファイルのあるフォルダ（画像のパスはここからの相対にする）。</summary>
    public required string PanelDir;
    /// <summary>PNG を書き出すフォルダ。</summary>
    public required string OutputDir;
    public string BaseName = "base";
    public List<string> BaseLayers = [];
    public List<string> LampLayers = [];
    public List<PsdNumberSpec> Numbers = [];
    /// <summary>表示灯・数字の Layer の値。</summary>
    public int Layer = 1;
}

/// <summary>書き出す PNG。</summary>
public sealed record PsdOutputFile(string FullPath, byte[] Png);

public sealed class PsdBaseResult
{
    public string DayPath = "";
    public string? NightPath;
    public int Width, Height;
    public List<string> Layers = [];
}

public sealed class PsdImportResult
{
    public List<PsdOutputFile> Files = [];
    public PsdBaseResult? Base;
    /// <summary>パネルに足す計器（表示灯・数字）。グループの計器は連続して並んでいる。</summary>
    public List<PanelSection> Sections = [];
    public List<string> Warnings = [];
}

/// <summary>PSD のレイヤーからパネルの部品（PNG とセクション）を作る。</summary>
public static class PsdImport
{
    public static PsdImportResult Build(PsdImportSpec spec)
    {
        var r = new PsdImportResult();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 昼「名前.png」と夜「名前_N.png」の両方がほかとぶつからない名前にする（「戸」と「戸_N」が両方あっても上書きしない）
        string Unique(string baseName)
        {
            var n = SafeFileName(baseName);
            var name = n;
            for (int i = 2; used.Contains(name) || used.Contains(name + "_N"); i++) name = $"{n}_{i}";
            used.Add(name);
            used.Add(name + "_N");
            return name;
        }
        string Rel(string full) => PathFrom(spec.PanelDir, full);

        // ---- ベース ----
        if (spec.BaseLayers.Count > 0 && ComposeBase(spec.Day, spec.BaseLayers, r.Warnings, "昼") is { } dayImg)
        {
            var name = Unique(spec.BaseName);
            var dayPath = Path.Combine(spec.OutputDir, name + ".png");
            r.Files.Add(new(dayPath, PngWriter.EncodePow2(dayImg)));
            r.Base = new PsdBaseResult { DayPath = Rel(dayPath), Width = dayImg.Width, Height = dayImg.Height, Layers = spec.BaseLayers.ToList() };
            if (spec.Night != null)
            {
                // 夜は、同じ名前のレイヤーが何枚あっても全部重ねる（Photoshop で見えている絵と同じにする）
                var nightImg = ComposeBase(spec.Night, spec.BaseLayers, r.Warnings, "夜", allSameName: true);
                if (nightImg != null && spec.BaseLayers.Any(p => spec.Night.Find(p) != null))
                {
                    var np = Path.Combine(spec.OutputDir, name + "_N.png");
                    r.Files.Add(new(np, PngWriter.EncodePow2(nightImg)));
                    r.Base.NightPath = Rel(np);
                }
            }
        }

        // ---- 表示灯・数字（PSD の下から順。同じフォルダのものは続けて並べる） ----
        var items = new List<(int Order, string? Group, PanelSection Section)>();

        foreach (var path in spec.LampLayers)
        {
            var lamp = RenderLamp(spec.Day, spec.Night, path, r.Warnings);
            if (lamp == null) continue;
            var layer = spec.Day.Find(path) ?? spec.Night?.Find(path);
            var leaf = layer?.Name ?? Leaf(PsdFile.StripDuplicateMark(path));
            var name = Unique(leaf);
            var sec = PanelSection.Create(SectionKind.PilotLamp);
            sec.SetTitle(leaf);
            sec.Set("Subject", GuessSubject(leaf));
            sec.Set("Location", PanelValues.Point(lamp.Rect.Left, lamp.Rect.Top));
            var dp = Path.Combine(spec.OutputDir, name + ".png");
            r.Files.Add(new(dp, PngWriter.EncodePow2(lamp.Day)));
            sec.Set("DaytimeImage", Rel(dp));
            if (lamp.Night != null)
            {
                var np = Path.Combine(spec.OutputDir, name + "_N.png");
                r.Files.Add(new(np, PngWriter.EncodePow2(lamp.Night)));
                sec.Set("NighttimeImage", Rel(np));
            }
            sec.Set("Layer", spec.Layer.ToString());
            sec.PsdLayers.Add(path);
            items.Add((layer?.Index ?? 0, layer?.Parent?.Name, sec));
        }

        foreach (var num in spec.Numbers.Where(n => n.Frames.Count > 0))
        {
            var strip = RenderNumber(spec.Day, spec.Night, num.Frames, r.Warnings);
            if (strip == null) continue;
            var title = num.Name.Length > 0 ? num.Name : Leaf(PsdFile.StripDuplicateMark(num.Frames[0]));
            var name = Unique(title);
            var sec = PanelSection.Create(SectionKind.DigitalNumber);
            sec.SetTitle(title);
            sec.Set("Subject", GuessSubject(title));
            sec.Set("Location", PanelValues.Point(strip.Rect.Left, strip.Rect.Top));
            var dp = Path.Combine(spec.OutputDir, name + ".png");
            r.Files.Add(new(dp, PngWriter.EncodePow2(strip.Day)));
            sec.Set("DaytimeImage", Rel(dp));
            if (strip.Night != null)
            {
                var np = Path.Combine(spec.OutputDir, name + "_N.png");
                r.Files.Add(new(np, PngWriter.EncodePow2(strip.Night)));
                sec.Set("NighttimeImage", Rel(np));
            }
            sec.Set("Layer", spec.Layer.ToString());
            sec.Set("Interval", strip.Rect.Height.ToString());
            sec.PsdLayers.AddRange(num.Frames);
            var layers = num.Frames.Select(f => spec.Day.Find(f) ?? spec.Night?.Find(f)).ToList();
            var parents = layers.Select(l => l?.Parent?.Name).Distinct().ToList();
            items.Add((layers.Min(l => l?.Index ?? 0), parents.Count == 1 ? parents[0] : null, sec));
        }

        // フォルダ（グループ）ごとに、最初に出てくる位置へまとめる
        var groupOrder = items.Where(i => i.Group != null).GroupBy(i => i.Group!).ToDictionary(g => g.Key, g => g.Min(i => i.Order));
        foreach (var it in items.OrderBy(i => i.Group != null ? groupOrder[i.Group] : i.Order).ThenBy(i => i.Group ?? "").ThenBy(i => i.Order))
        {
            it.Section.SetGroups(it.Group != null ? [it.Group] : []);
            r.Sections.Add(it.Section);
        }
        return r;
    }

    // ================= 部品を作る（更新でも使う） =================

    /// <summary>キャンバス大に重ねたベース。PSD にどのレイヤーもなければ null。</summary>
    /// <param name="allSameName">
    /// true なら、同じ名前（「#2」を除く）のレイヤーを全部重ねる。夜の PSD のように、昼で選んだ名前から探すときに使う。
    /// </param>
    public static PsdPixels? ComposeBase(PsdFile psd, IEnumerable<string> paths, List<string> warnings, string label, bool allSameName = false)
    {
        var layers = new List<PsdLayer>();
        foreach (var p in paths)
        {
            var found = allSameName ? psd.FindAllSameName(p) : psd.Find(p) is { } f ? [f] : new List<PsdLayer>();
            if (found.Count == 0) { if (label == "昼") warnings.Add($"ベースのレイヤー「{p}」が{label}の PSD にありません"); continue; }
            foreach (var l in found)
            {
                if (l.IsFolder) layers.AddRange(psd.ComposableLayers.Where(x => IsInside(x, l)));
                else layers.Add(l);
            }
        }
        if (layers.Count == 0) return null;
        var w = new List<string>();
        var img = PsdCompose.Compose(psd, layers.Distinct(), psd.Canvas, 1, w);
        warnings.AddRange(w.Distinct().Select(x => $"{label}: {x}"));
        return img;
    }

    private static bool IsInside(PsdLayer l, PsdLayer folder)
    {
        for (var p = l.Parent; p != null; p = p.Parent) if (p == folder) return true;
        return false;
    }

    public sealed record LampImages(PsdRect Rect, PsdPixels Day, PsdPixels? Night);

    /// <summary>
    /// 表示灯 1 つぶん。昼と夜で範囲を合わせた同じ大きさで切り出す。夜にしかなければ昼は透明。
    /// </summary>
    public static LampImages? RenderLamp(PsdFile day, PsdFile? night, string path, List<string> warnings)
    {
        var d = day.Find(path);
        var n = night?.Find(path);
        if (d is { HasPixels: false }) d = null;
        if (n is { HasPixels: false }) n = null;
        if (d == null && n == null) { warnings.Add($"「{path}」は画像のあるレイヤーではありません"); return null; }
        // レイヤー効果（影・光彩など）が広がる分も含める
        var rect = (d != null ? PsdEffects.VisualBounds(d) : default).Union(n != null ? PsdEffects.VisualBounds(n) : default).Intersect(day.Canvas);
        if (rect.IsEmpty) { warnings.Add($"「{path}」はキャンバスの外にあります"); return null; }
        var w = new List<string>();
        var dayImg = d != null ? PsdCompose.RenderLayer(day, d, rect, w) : Transparent(rect);
        var nightImg = n != null ? PsdCompose.RenderLayer(night!, n, rect, w) : null;
        warnings.AddRange(w.Distinct());
        return new LampImages(rect, dayImg, nightImg);
    }

    public sealed record NumberImages(PsdRect Rect, int Frames, PsdPixels Day, PsdPixels? Night);

    /// <summary>デジタル数字のコマ画像。全コマの範囲を合わせた大きさで、上から順に縦に並べる。</summary>
    public static NumberImages? RenderNumber(PsdFile day, PsdFile? night, IReadOnlyList<string> frames, List<string> warnings)
    {
        var dl = frames.Select(f => day.Find(f) is { HasPixels: true } x ? x : null).ToList();
        var nl = frames.Select(f => night?.Find(f) is { HasPixels: true } x ? x : null).ToList();
        var rect = default(PsdRect);
        foreach (var l in dl.Concat(nl)) if (l != null) rect = rect.Union(PsdEffects.VisualBounds(l));
        rect = rect.Intersect(day.Canvas);
        if (rect.IsEmpty) { warnings.Add($"数字のコマ「{string.Join(" | ", frames)}」に画像がありません"); return null; }
        for (int i = 0; i < frames.Count; i++)
            if (dl[i] == null && nl[i] == null) warnings.Add($"数字のコマ「{frames[i]}」が見つかりません（透明なコマにします）");
        var w = new List<string>();
        var dayImg = Strip(day, dl, rect, w);
        var nightImg = night != null && nl.Any(l => l != null) ? Strip(night, nl, rect, w) : null;
        warnings.AddRange(w.Distinct());
        return new NumberImages(rect, frames.Count, dayImg, nightImg);
    }

    private static PsdPixels Strip(PsdFile psd, List<PsdLayer?> layers, PsdRect rect, List<string> warnings)
    {
        int fw = rect.Width, fh = rect.Height;
        var bgra = new byte[fw * fh * layers.Count * 4];
        for (int i = 0; i < layers.Count; i++)
        {
            if (layers[i] == null) continue;
            var f = PsdCompose.RenderLayer(psd, layers[i]!, rect, warnings);
            Buffer.BlockCopy(f.Bgra, 0, bgra, i * fw * fh * 4, f.Bgra.Length);
        }
        return new PsdPixels(new PsdRect(0, 0, fw, fh * layers.Count), bgra);
    }

    private static PsdPixels Transparent(PsdRect rect) => new(rect, new byte[rect.Width * rect.Height * 4]);

    /// <summary>
    /// 開いたときの役割。表示中の画像レイヤーはベース、非表示の小さな画像レイヤーは表示灯（点灯状態を非表示レイヤーで持つ作り方に合わせる）。
    /// 非表示でもキャンバスの 2% を超える大きなレイヤーは、差し替え用の別バージョンのことが多いので使わない。画像のないレイヤーも使わない。
    /// </summary>
    public static PsdRole DefaultRole(PsdLayer layer, PsdRect canvas)
    {
        // 再現できる調整レイヤー・べた塗りは、表示されていればベースに重ねる
        if (!layer.HasPixels) return layer.CanCompose && !layer.EffectivelyHidden ? PsdRole.Base : PsdRole.None;
        if (!layer.EffectivelyHidden) return PsdRole.Base;
        var r = layer.Bounds.Intersect(canvas);
        return (long)r.Width * r.Height <= (long)canvas.Width * canvas.Height / 50 ? PsdRole.Lamp : PsdRole.None;
    }

    /// <summary>レイヤーの注意書き（効果・描画モード・種類）。なければ空。</summary>
    public static string Notes(PsdLayer l)
    {
        var n = new List<string>();
        if (l.Kind == PsdLayerKind.Adjustment)
            n.Add(l.Adjustment != null ? $"調整レイヤー（{l.Adjustment.Name}。ベースに重ねるときだけ効きます）" : $"調整レイヤー（{PsdAdjust.Label(l.SpecialKey ?? "")}）は使えません");
        if (l.Kind == PsdLayerKind.Fill)
            n.Add(l.SolidColor != null ? "べた塗り" : "グラデーション・パターンの塗りつぶしは使えません");
        if (l.Kind == PsdLayerKind.Text) n.Add("文字");
        if (l.Kind == PsdLayerKind.Smart) n.Add("スマートオブジェクト");
        if (l.HasEffects) n.Add(PsdEffects.Summary(l));
        if (!l.IsFolder && !PsdCompose.IsNormalBlend(l.BlendMode)) n.Add($"描画モード: {PsdCompose.BlendLabel(l.BlendMode)}");
        if (l.Clipped) n.Add("クリッピング");
        if (l.HasMask && !l.MaskDisabled) n.Add("マスク");
        return string.Join("・", n);
    }

    // ================= 名前 =================

    public static string Leaf(string path) => path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;

    /// <summary>ファイル名に使えない文字を置き換える。</summary>
    public static string SafeFileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(name.Trim().Select(c => bad.Contains(c) || c is '|' or ';' or '#' ? '_' : c).ToArray()).Trim('.', ' ');
        return s.Length == 0 ? "layer" : s;
    }

    /// <summary>レイヤー名が状態量（ats22、door など）で始まっていれば、それを Subject にする。</summary>
    public static string GuessSubject(string name)
    {
        var t = name.Trim();
        if (Subjects.IsKnown(t)) return t.ToLowerInvariant();
        var first = t.Split([' ', '_', '　', '-', ':', '：'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return Subjects.IsKnown(first) ? first.ToLowerInvariant() : "";
    }

    /// <summary>パネルのフォルダから見た相対パス（ドライブが違えばそのまま）。</summary>
    public static string PathFrom(string baseDir, string full)
    {
        var rel = Path.GetRelativePath(baseDir, full);
        return Path.IsPathRooted(rel) ? full : rel;
    }
}
