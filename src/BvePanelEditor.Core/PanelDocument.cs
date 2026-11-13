namespace BvePanelEditor.Core;

public enum PanelLineKind { Blank, Comment, KeyValue, Unknown }

/// <summary>パネルファイルの 1 行。値を変えていない行は元の文字列のまま書き戻す。</summary>
public sealed class PanelLine
{
    public PanelLineKind Kind { get; private set; }
    public string Raw { get; private set; } = "";
    public string Key { get; private set; } = "";
    public string Value { get; private set; } = "";

    // KeyValue 行で、値の前（"Key = "）と後（空白＋コメント）の元の文字列
    private string _prefix = "";
    private string _suffix = "";

    public string CommentText
    {
        get
        {
            int ci = CommentIndex(Raw);
            return ci < 0 ? "" : Raw[(ci + 1)..].Trim();
        }
    }

    /// <summary>
    /// コメントの始まり（; か #）。ただし「Key = #0000FF」のように、= のすぐ後ろ（空白はよい）の「#」に 16 進の 3・6・8 桁が続くものは、
    /// 色の値として扱いコメントにしない（BVE4 の書式から使われている書き方で、BVE も色として読む）。
    /// </summary>
    public static int CommentIndex(string raw)
    {
        int a = raw.IndexOf(';'), b = -1;
        for (int i = raw.IndexOf('#'); i >= 0; i = raw.IndexOf('#', i + 1))
            if (!IsColorHash(raw, i)) { b = i; break; }
        if (a < 0) return b;
        if (b < 0) return a;
        return Math.Min(a, b);
    }

    /// <summary>raw[i] の「#」が「= #RRGGBB」の色の値の頭か。</summary>
    private static bool IsColorHash(string raw, int i)
    {
        int j = i - 1;
        while (j >= 0 && char.IsWhiteSpace(raw[j])) j--;
        if (j < 0 || raw[j] != '=' || raw.IndexOf('=') != j) return false;
        int k = i + 1;
        while (k < raw.Length && Uri.IsHexDigit(raw[k])) k++;
        int n = k - i - 1;
        if (n is not (3 or 6 or 8)) return false;
        return k == raw.Length || char.IsWhiteSpace(raw[k]) || raw[k] is ';' or '#';
    }

    public static PanelLine Parse(string raw)
    {
        var line = new PanelLine { Raw = raw };
        int ci = CommentIndex(raw);
        string content = ci < 0 ? raw : raw[..ci];
        if (content.Trim().Length == 0)
        {
            line.Kind = ci < 0 ? PanelLineKind.Blank : PanelLineKind.Comment;
            return line;
        }
        int eq = content.IndexOf('=');
        if (eq < 0)
        {
            line.Kind = PanelLineKind.Unknown;
            return line;
        }
        line.Kind = PanelLineKind.KeyValue;
        line.Key = content[..eq].Trim();
        int vs = eq + 1;
        while (vs < content.Length && char.IsWhiteSpace(content[vs])) vs++;
        line.Value = content[vs..].TrimEnd();
        line._prefix = raw[..vs];
        line._suffix = raw[(vs + line.Value.Length)..];
        return line;
    }

    public static PanelLine Create(string key, string value)
    {
        var l = Parse($"{key} = {value}");
        return l;
    }

    public static PanelLine Blank() => Parse("");

    public void SetValue(string value)
    {
        if (Kind != PanelLineKind.KeyValue) throw new InvalidOperationException("KeyValue 行ではありません");
        Value = value.Trim();
        Raw = _prefix + Value + _suffix;
    }

    public PanelLine Clone() => Parse(Raw);

    public override string ToString() => Raw;
}

public enum SectionKind { This, PilotLamp, Needle, DigitalGauge, DigitalNumber, Unknown }

/// <summary>[セクション] 1 つ。直前のコメント行（説明書き）もこのセクションに付けて持つ。</summary>
public sealed class PanelSection
{
    public List<PanelLine> Leading { get; } = [];
    public string HeaderRaw { get; set; } = "";
    public List<PanelLine> Lines { get; } = [];

    /// <summary>
    /// 入っているグループの名前（外側から順。グループの中のグループに入っていれば 2 つ以上）。
    /// ファイルでは開始行・終了行のコメントで囲んで表す。
    /// </summary>
    public List<string> Groups { get; } = [];

    /// <summary>
    /// 入るバリエーションの条件（「A, B」「!A」の書き方。null なら共通＝すべてのバリエーション）。
    /// ファイルではオブジェクトの前に「; @group 条件」、共通に戻るところに「; @end」と書く。
    /// </summary>
    public string? VariationCond { get; set; }

    /// <summary>バリエーション name に入るか。</summary>
    public bool InVariation(string name) => VariationCondition.Match(VariationCond, name);

    /// <summary>いちばん内側のグループ（なければ null）。</summary>
    public string? Group => Groups.Count > 0 ? Groups[^1] : null;

    /// <summary>いちばん外側のグループ（なければ null）。</summary>
    public string? TopGroup => Groups.Count > 0 ? Groups[0] : null;

    /// <summary>入っているグループを、ほかのオブジェクトと同じにする（外側から順）。</summary>
    public void SetGroups(IEnumerable<string> groups)
    {
        var list = groups.ToList();
        Groups.Clear();
        Groups.AddRange(list);
    }

    /// <summary>このオブジェクトの画像を作った PSD のレイヤー（「フォルダ/レイヤー」の形）。ファイルでは見出しのすぐ下の「;@PSDレイヤー: 名前」。</summary>
    public List<string> PsdLayers { get; } = [];

    /// <summary>どの PSD から作ったか（PanelDocument.PsdSources の番号。ふつうは 1）。ファイルでは「;@PSDレイヤー #2: 名前」。</summary>
    public int PsdSource { get; set; } = 1;

    /// <summary>画像を切り抜いた元の組の名前（PanelDocument.CropSets）。なければ null。ファイルでは見出しのすぐ下の「;@切り抜き: 発光」。</summary>
    public string? CropSet { get; set; }

    /// <summary>
    /// メインパネル（パネルの土台にする、常表示の表示灯）か。[This] の画像は透明にして、この表示灯を土台に使う
    /// （メインパネルより下に窓などを置けるように）。ファイルでは見出しのすぐ下の「;@メインパネル」。
    /// </summary>
    public bool IsMainPanel { get; set; }

    /// <summary>パネルくんの画面で隠しているか（表示／非表示の切り替え）。ファイルには書かない（BVE では表示される）。</summary>
    public bool ViewHidden { get; set; }

    /// <summary>
    /// コメントアウトして無効にしたオブジェクト（BVE でも表示されない）。ファイルでは見出しと中身の行の頭に「;」を付けた形
    /// （「;[Needle]」「;Subject = kmph」。中の空行は「;」だけの行）。HeaderRaw と Lines は「;」を外した形で持ち、書き出すときに付ける。
    /// </summary>
    public bool Disabled { get; set; }

    /// <summary>無効にしたオブジェクトのかたまりの後ろの行（空行など。「;」を付けずにそのまま書く）。</summary>
    public List<PanelLine> After { get; } = [];

    /// <summary>無効にする（中身の末尾の空行はかたまりの外へ出す）。</summary>
    public void Disable()
    {
        if (Disabled || Kind == SectionKind.This) return;
        int tail = Lines.Count;
        while (tail > 0 && Lines[tail - 1].Kind == PanelLineKind.Blank) tail--;
        After.InsertRange(0, Lines.Skip(tail));
        Lines.RemoveRange(tail, Lines.Count - tail);
        Disabled = true;
    }

    /// <summary>有効に戻す（かたまりの後ろの行は中身の後ろへ戻す）。</summary>
    public void Enable()
    {
        if (!Disabled) return;
        Lines.AddRange(After);
        After.Clear();
        Disabled = false;
    }

    /// <summary>切り抜く形（丸・多角形。画像の左上から）。空なら四角のまま。ファイルでは「;@切り抜き形: 丸 32,32 r=28」。</summary>
    public List<Crop.CropShape> CropShapes { get; } = [];

    public string Name
    {
        get
        {
            int a = HeaderRaw.IndexOf('['), b = HeaderRaw.IndexOf(']');
            return a >= 0 && b > a ? HeaderRaw[(a + 1)..b].Trim() : HeaderRaw.Trim();
        }
    }

    public SectionKind Kind => KindFromName(Name);

    public static SectionKind KindFromName(string name) => name.ToLowerInvariant() switch
    {
        "this" => SectionKind.This,
        "pilotlamp" => SectionKind.PilotLamp,
        "needle" => SectionKind.Needle,
        "digitalgauge" => SectionKind.DigitalGauge,
        "digitalnumber" => SectionKind.DigitalNumber,
        _ => SectionKind.Unknown,
    };

    public static PanelSection Create(SectionKind kind)
    {
        var s = new PanelSection { HeaderRaw = $"[{kind}]" };
        s.Leading.Add(PanelLine.Blank());
        return s;
    }

    /// <summary>キーの値（大文字小文字は区別しない。重複していたら最後のもの）。</summary>
    public string? Get(string key)
    {
        for (int i = Lines.Count - 1; i >= 0; i--)
        {
            var l = Lines[i];
            if (l.Kind == PanelLineKind.KeyValue && l.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return l.Value;
        }
        return null;
    }

    public PanelLine? GetLine(string key)
    {
        for (int i = Lines.Count - 1; i >= 0; i--)
        {
            var l = Lines[i];
            if (l.Kind == PanelLineKind.KeyValue && l.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return l;
        }
        return null;
    }

    /// <summary>値を設定する。null または空文字ならそのキーの行を消す。</summary>
    public void Set(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Lines.RemoveAll(l => l.Kind == PanelLineKind.KeyValue && l.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            return;
        }
        var existing = GetLine(key);
        if (existing != null)
        {
            existing.SetValue(value);
            return;
        }
        // 最後の中身のある行の後ろ（末尾の空行・コメントより前）に足す
        int insert = 0;
        for (int i = Lines.Count - 1; i >= 0; i--)
        {
            if (Lines[i].Kind is PanelLineKind.KeyValue or PanelLineKind.Unknown)
            {
                insert = i + 1;
                break;
            }
        }
        Lines.Insert(insert, PanelLine.Create(key, value));
    }

    public IEnumerable<PanelLine> KeyValues => Lines.Where(l => l.Kind == PanelLineKind.KeyValue);

    // ---- 名前（直前のコメント） ----
    //
    // 名前は次の順に探す:
    //   1. 見出し行の後ろのコメント           "[PilotLamp]##計器灯"
    //   2. 見出しのすぐ上の行のコメント       ";速度計" / ";;次駅停車" / ";----< ATS電源 >----"
    // 見出し行に名前がある書き方では、すぐ上の行は「;----< ATS関連 >----」のようなグループの区切りなので 1 を優先する。
    // 空行をはさんだコメントは名前にしない（実データではほぼ無効化したキー "##Layer = 5"）。
    // 書き換えるときは、名前の前後の飾り（";;" や ";----< "、" >----"）はそのまま残す。

    /// <summary>飾りの線に使われる記号。2 文字以上続いたときだけ飾りとみなす（「_pos」「-ATS」の 1 文字は名前の一部）。</summary>
    private const string DecoMarks = "/-=*<>_~+|;#";

    private static bool IsSpace(char c) => char.IsWhiteSpace(c) || c == '　';

    /// <summary>1 行からコメント部分の「前の飾り」「名前」を取り出す。名前として使えなければ false。</summary>
    public static bool TrySplitTitle(string raw, out string prefix, out string title) =>
        TrySplitTitle(raw, out prefix, out title, out _);

    /// <summary>1 行からコメント部分の「前の飾り」「名前」「後ろの飾り」を取り出す。名前として使えなければ false。</summary>
    public static bool TrySplitTitle(string raw, out string prefix, out string title, out string suffix)
    {
        prefix = title = suffix = "";
        int ci = PanelLine.CommentIndex(raw);
        if (ci < 0) return false;
        // 前: コメント記号（; #）と空白は全部、続く飾りの記号は 2 文字以上のときだけ読み飛ばす
        int k = ci;
        while (k < raw.Length && (raw[k] is ';' or '#' || IsSpace(raw[k]))) k++;
        int d = k;
        while (d < raw.Length && DecoMarks.Contains(raw[d])) d++;
        if (d - k >= 2 || d == raw.Length) { k = d; while (k < raw.Length && IsSpace(raw[k])) k++; }
        // 後ろ: 空白は全部、その前の飾りの記号は 2 文字以上のときだけ読み飛ばす
        int e = raw.Length;
        while (e > k && IsSpace(raw[e - 1])) e--;
        int t = e;
        while (t > k && DecoMarks.Contains(raw[t - 1])) t--;
        if (e - t >= 2) { e = t; while (e > k && IsSpace(raw[e - 1])) e--; }
        title = raw[k..e];
        prefix = raw[..k];
        suffix = raw[e..];
        if (title.Length == 0) return false;
        // 無効化したキー（";Layer = 5"）は名前ではない
        if (System.Text.RegularExpressions.Regex.IsMatch(title, @"^[A-Za-z][A-Za-z0-9]*\s*=")) return false;
        return true;
    }

    private bool LeadingTitle(out string prefix, out string title, out string suffix)
    {
        prefix = title = suffix = "";
        if (Leading.Count == 0 || Leading[^1].Kind != PanelLineKind.Comment) return false;
        return TrySplitTitle(Leading[^1].Raw, out prefix, out title, out suffix);
    }

    /// <summary>名前がどこに書いてあるか。</summary>
    public string TitleSource =>
        TrySplitTitle(HeaderRaw, out _, out _) ? "見出し行" : LeadingTitle(out _, out _, out _) ? "直前の行" : "";

    /// <summary>オブジェクトの名前（直前のコメント）。なければ空。</summary>
    public string Title
    {
        get
        {
            if (TrySplitTitle(HeaderRaw, out _, out var h)) return h;
            return LeadingTitle(out _, out var t, out _) ? t : "";
        }
    }

    /// <summary>名前を書き換える。空なら名前の行（見出し行の場合はコメント部分）を消す。</summary>
    public void SetTitle(string? title)
    {
        title = (title ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (TrySplitTitle(HeaderRaw, out var hp, out _, out var hs))
        {
            if (title.Length == 0) HeaderRaw = HeaderRaw[..PanelLine.CommentIndex(HeaderRaw)].TrimEnd();
            else HeaderRaw = hp + title + hs;
            return;
        }
        if (LeadingTitle(out var prefix, out _, out var suffix))
        {
            if (title.Length == 0) Leading.RemoveAt(Leading.Count - 1);
            else Leading[^1] = PanelLine.Parse(prefix + title + suffix);
            return;
        }
        if (title.Length == 0) return;
        // 見出しと同じ字下げで ";名前" を見出しのすぐ上に足す
        int indent = 0;
        while (indent < HeaderRaw.Length && char.IsWhiteSpace(HeaderRaw[indent])) indent++;
        Leading.Add(PanelLine.Parse(HeaderRaw[..indent] + ";" + title));
    }

    public PanelSection Clone()
    {
        var s = new PanelSection { HeaderRaw = HeaderRaw, PsdSource = PsdSource, CropSet = CropSet, IsMainPanel = IsMainPanel, ViewHidden = ViewHidden, Disabled = Disabled, VariationCond = VariationCond };
        s.Groups.AddRange(Groups);
        s.After.AddRange(After.Select(l => l.Clone()));
        s.PsdLayers.AddRange(PsdLayers);
        s.CropShapes.AddRange(CropShapes.Select(c => c.Clone()));
        s.Leading.AddRange(Leading.Select(l => l.Clone()));
        s.Lines.AddRange(Lines.Select(l => l.Clone()));
        return s;
    }
}

/// <summary>セクションが本文の何行目にあるか（1 始まり）。</summary>
public readonly record struct SectionLineRange(int FirstLine, int HeaderLine, int LastLine);

/// <summary>本文の 1 行が何の行か。</summary>
public enum LineRole { FileHeader, Preamble, Leading, SectionHeader, Body, GroupBegin, GroupEnd, VariationMark }

/// <summary>画像を作った PSD（昼と夜の組）。パスはパネルファイルからの相対パス。</summary>
public sealed class PsdSource
{
    public int Id { get; set; } = 1;
    public string Day { get; set; } = "";
    public string? Night { get; set; }
}

/// <summary>表示灯を切り抜く元の画像の組（「発光」「扉」など）。パスはパネルファイルからの相対パス。</summary>
public sealed class CropSet
{
    public string Name { get; set; } = "発光";
    public string Day { get; set; } = "";
    public string? Night { get; set; }
    /// <summary>昼が PSD のとき、重ねるレイヤー（パス）。画像ファイルなら null。ファイルでは「;@切り抜き元レイヤー 発光 昼: a | b」。</summary>
    public List<string>? DayLayers { get; set; }
    public List<string>? NightLayers { get; set; }
    public bool HasLayers => DayLayers != null || NightLayers != null;
    /// <summary>切り抜く前に、透明な所へメインパネル（の画像）を重ねる。ファイルでは見出しの「| メインパネルに重ねる」。</summary>
    public bool OverMain { get; set; }
    public const string OverMainMark = "メインパネルに重ねる";
}

/// <summary>グループの書き方の問題（Section は関係するオブジェクトの番号、なければ -1）。</summary>
public sealed record GroupWarning(int Section, string Message);

/// <summary>運転台パネルファイル全体。コメントや書式を崩さず読み書きする。</summary>
public sealed partial class PanelDocument
{
    public const string DefaultHeader = "BveTs Instrument Panel 2.00";
    public const string HeaderPrefix = "BveTs Instrument Panel";

    public string HeaderLine { get; set; } = DefaultHeader;
    public List<PanelLine> Preamble { get; } = [];
    public List<PanelSection> Sections { get; } = [];
    public string NewLine { get; set; } = "\r\n";
    public bool EndsWithNewLine { get; set; } = true;

    /// <summary>古い書式の見出し「Version 1.0」（BVE4 の panel2.cfg と同じ形。BVE5 でも読める）か。</summary>
    public bool IsLegacyHeader => IsLegacyHeaderLine(HeaderLine);

    public static bool IsLegacyHeaderLine(string line) =>
        System.Text.RegularExpressions.Regex.IsMatch(line.TrimStart('﻿'), @"^\s*Version\s*=?\s*1(\.\d+)?\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public bool HasPanelHeader => HeaderLine.TrimStart('﻿', ' ', '\t').StartsWith(HeaderPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>"2.00" など。読めなければ空。</summary>
    public string Version
    {
        get
        {
            var h = HeaderLine.Trim();
            if (!HasPanelHeader) return "";
            var rest = h[HeaderPrefix.Length..];
            int c = rest.IndexOf(':');
            if (c >= 0) rest = rest[..c];
            return rest.Trim();
        }
    }

    /// <summary>書式 2.01 で追加されたキー（Tilt・StopPin・Perspective。BVE 5.8 と BVE6 で使える）が使える版か。</summary>
    public bool SupportsV201 => double.TryParse(Version, System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 2.01 - 1e-9;

    public PanelSection? This => Sections.FirstOrDefault(s => s.Kind == SectionKind.This && !s.Disabled);

    /// <summary>メインパネル（「;@メインパネル」の付いた表示灯）。2 つ以上あれば最後のもの。なければ null。</summary>
    public PanelSection? MainPanel => Sections.LastOrDefault(s => s.IsMainPanel && s.Kind == SectionKind.PilotLamp && !s.Disabled);

    /// <summary>
    /// 画像を作った PSD（1 つのパネルで複数使える）。見出しのすぐ下に「;===== PSD 昼: パス =====」（2 つ目からは「PSD 昼 #2」）。
    /// </summary>
    public List<PsdSource> PsdSources { get; } = [];

    public PsdSource? GetPsdSource(int id) => PsdSources.FirstOrDefault(s => s.Id == id);

    public PsdSource GetOrAddPsdSource(int id)
    {
        var s = GetPsdSource(id);
        if (s != null) return s;
        s = new PsdSource { Id = id };
        PsdSources.Add(s);
        PsdSources.Sort((a, b) => a.Id.CompareTo(b.Id));
        return s;
    }

    /// <summary>1 つ目の PSD（昼）。</summary>
    public string? PsdDay
    {
        get => GetPsdSource(1)?.Day is { Length: > 0 } d ? d : null;
        set
        {
            if (value == null) { PsdSources.RemoveAll(s => s.Id == 1); return; }
            GetOrAddPsdSource(1).Day = value;
        }
    }

    /// <summary>1 つ目の PSD（夜）。</summary>
    public string? PsdNight
    {
        get => GetPsdSource(1)?.Night;
        set { if (value != null || GetPsdSource(1) != null) GetOrAddPsdSource(1).Night = value; }
    }

    /// <summary>
    /// 表示灯を切り抜いた元の画像の組。見出しのすぐ下に「;===== 切り抜き元 発光: 昼=a.png | 夜=b.png =====」。
    /// </summary>
    public List<CropSet> CropSets { get; } = [];

    public bool HasCropLink => CropSets.Count > 0 && (Sections.Any(s => s.CropSet != null) || CropSets.Any(c => c.Name == CropSetBaseName && c.HasLayers));

    /// <summary>パネル本体（[This] の昼・夜画像）を PSD から重ねて書き出したときの切り抜き元の組の名前。</summary>
    public const string CropSetBaseName = "本体";

    public bool HasPsdLink => PsdSources.Any(p => p.Day.Length > 0) && Sections.Any(s => s.PsdLayers.Count > 0);

    public static PanelDocument Parse(string text)
    {
        var doc = new PanelDocument();
        doc.NewLine = text.Contains("\r\n") ? "\r\n" : text.Contains('\n') ? "\n" : "\r\n";
        var lines = BveText.SplitLines(text).ToList();
        doc.EndsWithNewLine = lines.Count > 1 && lines[^1].Length == 0;
        if (doc.EndsWithNewLine) lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0) return doc;

        doc.HeaderLine = lines[0].TrimStart('﻿');
        List<PanelLine> current = doc.Preamble;
        string? cond = null;               // 今の「; @group」の条件（null なら共通）
        int condLine = 0;
        var groups = new List<string>();  // 今読んでいるグループ（外側から順）
        var groupStarts = new List<int>(); // それぞれのグループの最初のオブジェクトの番号
        var cropLayers = new List<(string Name, bool Night, List<string> Layers, int Line)>();
        PanelSection? disabled = null; // 今読んでいる、コメントアウトしたオブジェクトのかたまり
        for (int i = 1; i < lines.Count; i++)
        {
            string raw = lines[i];
            // PSD とのつながりの行も本文の行として持たない（書き出すときに決まった場所へ出す）
            if (TryPsdFile(raw, out var night, out var srcId, out var psdPath))
            {
                var src = doc.GetOrAddPsdSource(srcId);
                if (night) src.Night = psdPath; else src.Day = psdPath;
                continue;
            }
            if (TryCropLayersLine(raw, out var clName, out var clNight, out var clLayers))
            {
                cropLayers.Add((clName, clNight, clLayers, i + 1));
                continue;
            }
            if (TryRagLine(raw, out var ragIni))
            {
                doc.RagIni = ragIni;
                continue;
            }
            if (TryVariationLine(raw, out var variation))
            {
                doc.Variations.RemoveAll(v => v.Name == variation.Name);
                doc.Variations.Add(variation);
                continue;
            }
            // バリエーションの印（「; @group 条件」〜「; @end」）は本文の行として持たず、次のオブジェクトの条件にする
            if (TryVariationMark(raw, out var isEnd, out var markCond))
            {
                if (isEnd)
                {
                    if (cond == null) doc.GroupWarnings.Add(new(-1, $"{i + 1} 行目: 対応する「@group」のない「@end」があります（無視します）"));
                    cond = null;
                }
                else if (VariationCondition.Parse(markCond) == null)
                {
                    doc.GroupWarnings.Add(new(-1, $"{i + 1} 行目: 「@group」の後ろにバリエーション名がありません（共通として扱います）"));
                    cond = null;
                }
                else
                {
                    cond = VariationCondition.Format(VariationCondition.Parse(markCond)!);
                    condLine = i + 1;
                }
                continue;
            }
            if (TryCropSetLine(raw, out var cropSet))
            {
                doc.CropSets.RemoveAll(c => c.Name == cropSet.Name);
                doc.CropSets.Add(cropSet);
                continue;
            }
            if (IsMainPanelLine(raw))
            {
                if (doc.Sections.Count > 0 && ReferenceEquals(current, doc.Sections[^1].Lines))
                {
                    if (doc.Sections[^1].Kind != SectionKind.PilotLamp)
                        doc.GroupWarnings.Add(new(doc.Sections.Count - 1, $"{i + 1} 行目: 「@メインパネル」は表示灯 [PilotLamp] にだけ付けられます（無視します）"));
                    else
                    {
                        if (doc.Sections.Any(s => s.IsMainPanel))
                            doc.GroupWarnings.Add(new(doc.Sections.Count - 1, $"{i + 1} 行目: 「@メインパネル」が 2 つ以上あります（最後のものをメインパネルにします）"));
                        doc.Sections[^1].IsMainPanel = true;
                    }
                }
                else doc.GroupWarnings.Add(new(-1, $"{i + 1} 行目: どのオブジェクトのものかわからない「@メインパネル」の行があります（無視します）"));
                continue;
            }
            if (TryCropShapeLine(raw, out var cropShape))
            {
                if (doc.Sections.Count > 0 && ReferenceEquals(current, doc.Sections[^1].Lines)) doc.Sections[^1].CropShapes.Add(cropShape);
                else doc.GroupWarnings.Add(new(-1, $"{i + 1} 行目: どのオブジェクトのものかわからない「@切り抜き形」の行があります（無視します）"));
                continue;
            }
            if (TryCropLine(raw, out var cropName))
            {
                if (doc.Sections.Count > 0 && ReferenceEquals(current, doc.Sections[^1].Lines)) doc.Sections[^1].CropSet = cropName;
                else doc.GroupWarnings.Add(new(-1, $"{i + 1} 行目: どのオブジェクトのものかわからない「@切り抜き」の行があります（無視します）"));
                continue;
            }
            if (TryPsdLayers(raw, out var layerSrc, out var psdLayers))
            {
                if (doc.Sections.Count > 0 && ReferenceEquals(current, doc.Sections[^1].Lines))
                {
                    doc.Sections[^1].PsdLayers.Clear();
                    doc.Sections[^1].PsdLayers.AddRange(psdLayers);
                    doc.Sections[^1].PsdSource = layerSrc;
                }
                else doc.GroupWarnings.Add(new(-1, $"{i + 1} 行目: どのオブジェクトのものかわからない「@PSDレイヤー」の行があります（無視します）"));
                continue;
            }
            // グループの開始行・終了行は本文の行として持たず、グループの切り替えだけに使う（書き出すときに作り直す）
            if (TryGroupEnd(raw, out var endName))
            {
                if (groups.Count == 0)
                    doc.GroupWarnings.Add(new(-1, $"{i + 1} 行目: 開始行のない「グループ終わり」があります（無視します）"));
                else
                {
                    // 名前が外側のグループのものなら、その内側のグループもここで閉じる
                    int close = endName.Length > 0 && groups[^1] != endName && groups.LastIndexOf(endName) is >= 0 and var at ? at : groups.Count - 1;
                    for (int k = groups.Count - 1; k >= close; k--)
                    {
                        int start = groupStarts[k];
                        if (k > close)
                            doc.GroupWarnings.Add(new(start < doc.Sections.Count ? start : -1, $"{i + 1} 行目: グループ「{groups[k]}」の「グループ終わり」の行がありません（外側の「{endName}」の終わりで閉じます）"));
                        else if (endName.Length > 0 && endName != groups[k])
                            doc.GroupWarnings.Add(new(start < doc.Sections.Count ? start : -1, $"{i + 1} 行目: グループ「{groups[k]}」の終わりの名前が「{endName}」になっています"));
                        if (start >= doc.Sections.Count)
                            doc.GroupWarnings.Add(new(-1, $"グループ「{groups[k]}」にオブジェクトがありません（無視します）"));
                    }
                    groups.RemoveRange(close, groups.Count - close);
                    groupStarts.RemoveRange(close, groupStarts.Count - close);
                }
                continue;
            }
            if (TryGroupBegin(raw, out var beginName))
            {
                // グループの中のグループ（入れ子）も使える
                groups.Add(beginName);
                groupStarts.Add(doc.Sections.Count);
                continue;
            }
            // コメントアウトしたオブジェクトの見出し（行頭の「;」のすぐ後ろが [PilotLamp] など）
            bool disabledHeader = IsDisabledSectionHeader(raw);
            if (!disabledHeader && disabled != null)
            {
                // かたまりの中: 行頭の「;」を外して中身として持つ（「;」だけの行は空行）
                if (raw.Length > 0 && raw[0] == ';')
                {
                    current.Add(PanelLine.Parse(raw[1..]));
                    continue;
                }
                // 空行や「;」で始まらない行でかたまりが終わる。その後ろの行は After に持つ
                current = disabled.After;
                disabled = null;
            }
            if (disabledHeader || IsSectionHeader(raw))
            {
                var sec = new PanelSection { HeaderRaw = disabledHeader ? raw[1..] : raw, Disabled = disabledHeader, VariationCond = cond };
                sec.Groups.AddRange(groups);
                // 直前の空行・コメントのかたまりはこのセクションの説明として持っていく（無効のかたまりの中身は持っていかない）
                int k = current.Count;
                if (disabled == null)
                    while (k > 0 && current[k - 1].Kind is PanelLineKind.Blank or PanelLineKind.Comment) k--;
                sec.Leading.AddRange(current.Skip(k));
                current.RemoveRange(k, current.Count - k);
                doc.Sections.Add(sec);
                current = sec.Lines;
                disabled = disabledHeader ? sec : null;
            }
            else
            {
                current.Add(PanelLine.Parse(raw));
            }
        }
        if (cond != null)
            doc.GroupWarnings.Add(new(-1, $"{condLine} 行目: 「@group {cond}」が「@end」で閉じられていません（ファイルの最後まで有効）"));
        for (int k = groups.Count - 1; k >= 0; k--)
            doc.GroupWarnings.Add(new(groupStarts[k] < doc.Sections.Count ? groupStarts[k] : -1, $"グループ「{groups[k]}」の「グループ終わり」の行がありません（ファイルの最後までをグループとして扱います）"));
        foreach (var (name, night, layers, line) in cropLayers)
        {
            var set = doc.CropSets.FirstOrDefault(c => c.Name == name);
            if (set == null || (night && set.Night == null))
                doc.GroupWarnings.Add(new(-1, $"{line} 行目: 切り抜き元「{name}」の{(night ? "夜" : "昼")}がないのに「@切り抜き元レイヤー」の行があります（無視します）"));
            else if (night) set.NightLayers = layers;
            else set.DayLayers = layers;
        }
        return doc;
    }

    /// <summary>読み込んだときに見つかったグループの書き方の問題。</summary>
    public List<GroupWarning> GroupWarnings { get; } = [];

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*[;#]+\s*=*\s*グループ\s*[:：]\s*(?<n>.*?)\s*=*\s*$")]
    private static partial System.Text.RegularExpressions.Regex GroupBeginRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*[;#]+\s*=*\s*グループ終わり\s*(?:[:：]\s*(?<n>.*?))?\s*=*\s*$")]
    private static partial System.Text.RegularExpressions.Regex GroupEndRegex();

    /// <summary>グループの開始行か（";===== グループ: 名前 =====" など）。</summary>
    public static bool TryGroupBegin(string raw, out string name)
    {
        var m = GroupBeginRegex().Match(raw);
        name = m.Success ? m.Groups["n"].Value.Trim() : "";
        return m.Success && name.Length > 0;
    }

    /// <summary>グループの終了行か（";===== グループ終わり: 名前 =====" など）。</summary>
    public static bool TryGroupEnd(string raw, out string name)
    {
        var m = GroupEndRegex().Match(raw);
        name = m.Success ? m.Groups["n"].Value.Trim() : "";
        return m.Success;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*[;#]+\s*=*\s*PSD\s*(?<w>昼|夜)\s*(?:#(?<id>\d+))?\s*[:：]\s*(?<p>.*?)\s*=*\s*$")]
    private static partial System.Text.RegularExpressions.Regex PsdFileRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*[;#]+\s*@PSDレイヤー\s*(?:#(?<id>\d+))?\s*[:：]\s*(?<v>.*?)\s*$")]
    private static partial System.Text.RegularExpressions.Regex PsdLayersRegex();

    /// <summary>「;===== PSD 昼: パス =====」「;===== PSD 夜 #2: パス =====」の行か。</summary>
    public static bool TryPsdFile(string raw, out bool night, out int id, out string path)
    {
        var m = PsdFileRegex().Match(raw);
        night = m.Success && m.Groups["w"].Value == "夜";
        id = m.Success && int.TryParse(m.Groups["id"].Value, out var n) && n > 0 ? n : 1;
        path = m.Success ? m.Groups["p"].Value.Trim() : "";
        return m.Success && path.Length > 0;
    }

    /// <summary>「;@PSDレイヤー: a | b」「;@PSDレイヤー #2: a」の行か。</summary>
    public static bool TryPsdLayers(string raw, out int id, out List<string> layers)
    {
        var m = PsdLayersRegex().Match(raw);
        id = m.Success && int.TryParse(m.Groups["id"].Value, out var n) && n > 0 ? n : 1;
        layers = m.Success ? m.Groups["v"].Value.Split('|').Select(x => x.Trim()).Where(x => x.Length > 0).ToList() : [];
        return m.Success;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*[;#]+\s*=*\s*切り抜き元\s+(?<n>[^:：]+?)\s*[:：]\s*(?<v>.*?)\s*=*\s*$")]
    private static partial System.Text.RegularExpressions.Regex CropSetRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*[;#]+\s*@切り抜き\s*[:：]\s*(?<n>.*?)\s*$")]
    private static partial System.Text.RegularExpressions.Regex CropRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*[;#]+\s*@切り抜き元レイヤー\s+(?<n>.+?)\s+(?<side>昼|夜)\s*[:：]\s*(?<v>.*?)\s*$")]
    private static partial System.Text.RegularExpressions.Regex CropLayersRegex();

    /// <summary>「;@切り抜き元レイヤー 発光 昼: a | b」の行か。</summary>
    public static bool TryCropLayersLine(string raw, out string name, out bool night, out List<string> layers)
    {
        var m = CropLayersRegex().Match(raw);
        name = m.Success ? m.Groups["n"].Value.Trim() : "";
        night = m.Success && m.Groups["side"].Value == "夜";
        layers = m.Success ? m.Groups["v"].Value.Split('|').Select(x => x.Trim()).Where(x => x.Length > 0).ToList() : [];
        return m.Success && name.Length > 0;
    }

    public static string CropLayersLine(string name, bool night, IEnumerable<string> layers) =>
        $";@切り抜き元レイヤー {name} {(night ? "夜" : "昼")}: {string.Join(" | ", layers)}";

    /// <summary>「;===== 切り抜き元 発光: 昼=a.png | 夜=b.png =====」の行か。</summary>
    public static bool TryCropSetLine(string raw, out CropSet set)
    {
        set = new CropSet();
        var m = CropSetRegex().Match(raw);
        if (!m.Success) return false;
        set.Name = m.Groups["n"].Value.Trim();
        foreach (var part in m.Groups["v"].Value.Split('|'))
        {
            if (part.Trim() == CropSet.OverMainMark) { set.OverMain = true; continue; }
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            var v = kv[1].Trim();
            if (kv[0].Trim() == "昼") set.Day = v;
            else if (kv[0].Trim() == "夜" && v.Length > 0) set.Night = v;
        }
        return set.Name.Length > 0 && set.Day.Length > 0;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*[;#]+\s*@切り抜き形\s*[:：]\s*(?<v>.*?)\s*$")]
    private static partial System.Text.RegularExpressions.Regex CropShapeRegex();

    /// <summary>「;@切り抜き形: 丸 32,32 r=28」の行か。形が読めなければ false（ふつうのコメントとして残す）。</summary>
    public static bool TryCropShapeLine(string raw, out Crop.CropShape shape)
    {
        shape = new Crop.CropShape();
        var m = CropShapeRegex().Match(raw);
        return m.Success && Crop.CropShape.TryParse(m.Groups["v"].Value, out shape);
    }

    public static string CropShapeLine(Crop.CropShape s) => $";@切り抜き形: {s.ToText()}";

    public const string MainPanelLine = ";@メインパネル";

    /// <summary>「;@メインパネル」の行か。</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*(?:;|#|//)\s*@(?<k>group|end)\b\s*(?<v>.*?)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex VariationMarkRegex();

    /// <summary>バリエーションの印「; @group 条件」「; @end」。</summary>
    public static bool TryVariationMark(string raw, out bool isEnd, out string cond)
    {
        var m = VariationMarkRegex().Match(raw);
        isEnd = m.Success && m.Groups["k"].Value.Equals("end", StringComparison.OrdinalIgnoreCase);
        cond = m.Success ? m.Groups["v"].Value : "";
        return m.Success;
    }

    public static string VariationMarkLine(string? cond) => cond == null ? "; @end" : $"; @group {cond}";

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*[;#]+\s*@バリエーション\s*[:：]\s*(?<n>[^=]+?)\s*=\s*(?<f>.*?)\s*$")]
    private static partial System.Text.RegularExpressions.Regex VariationLineRegex();

    /// <summary>バリエーションの一覧の行「;@バリエーション: 名前 = 出力ファイル」。</summary>
    public static bool TryVariationLine(string raw, out PanelVariation v)
    {
        var m = VariationLineRegex().Match(raw);
        v = new PanelVariation { Name = m.Success ? m.Groups["n"].Value.Trim() : "", File = m.Success ? m.Groups["f"].Value.Trim() : "" };
        return m.Success;
    }

    public static string VariationLine(PanelVariation v) => $";@バリエーション: {v.Name} = {v.File}";

    /// <summary>RealAnalogGauge の親 ini の行「;@RealAnalogGauge: パス」（パスはパネルファイルからの相対）。</summary>
    public static bool TryRagLine(string raw, out string path)
    {
        var m = System.Text.RegularExpressions.Regex.Match(raw, @"^\s*[;#]+\s*@RealAnalogGauge\s*[:：]\s*(?<p>.*?)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        path = m.Success ? m.Groups["p"].Value : "";
        return m.Success && path.Length > 0;
    }

    public static string RagLine(string path) => $";@RealAnalogGauge: {path}";

    public static bool IsMainPanelLine(string raw) => System.Text.RegularExpressions.Regex.IsMatch(raw, @"^\s*[;#]+\s*@メインパネル\s*$");

    /// <summary>「;@切り抜き: 発光」の行か。</summary>
    public static bool TryCropLine(string raw, out string name)
    {
        var m = CropRegex().Match(raw);
        name = m.Success ? m.Groups["n"].Value.Trim() : "";
        return m.Success && name.Length > 0;
    }

    public static string CropSetLine(CropSet s) =>
        $";===== 切り抜き元 {s.Name}: 昼={s.Day}{(s.Night != null ? $" | 夜={s.Night}" : "")}{(s.OverMain ? $" | {CropSet.OverMainMark}" : "")} =====";
    public static string CropLine(string name) => $";@切り抜き: {name}";

    private static string IdMark(int id) => id <= 1 ? "" : $" #{id}";
    public static string PsdFileLine(bool night, string path, int id = 1) => $";===== PSD {(night ? "夜" : "昼")}{IdMark(id)}: {path} =====";
    public static string PsdLayersLine(IEnumerable<string> layers, int id = 1) => $";@PSDレイヤー{IdMark(id)}: {string.Join(" | ", layers)}";

    public static string GroupBeginLine(string name) => $";===== グループ: {name} =====";
    public static string GroupEndLine(string name) => $";===== グループ終わり: {name} =====";

    /// <summary>
    /// コメントアウトしたオブジェクトの見出しか（行頭の「;」のすぐ後ろが [PilotLamp]・[Needle]・[DigitalGauge]・[DigitalNumber]）。
    /// [This] や知らない名前は、ふつうのコメントのまま。
    /// </summary>
    public static bool IsDisabledSectionHeader(string raw)
    {
        if (raw.Length < 3 || raw[0] != ';' || !IsSectionHeader(raw[1..])) return false;
        var rest = raw[1..];
        int ci = PanelLine.CommentIndex(rest);
        var content = (ci < 0 ? rest : rest[..ci]).Trim();
        if (content.Length == 0 || content[0] != '[' || raw[1] != '[') return false;
        var kind = PanelSection.KindFromName(content[1..^1].Trim());
        return kind is SectionKind.PilotLamp or SectionKind.Needle or SectionKind.DigitalGauge or SectionKind.DigitalNumber;
    }

    public static bool IsSectionHeader(string raw)
    {
        int ci = PanelLine.CommentIndex(raw);
        var content = (ci < 0 ? raw : raw[..ci]).Trim();
        return content.Length >= 2 && content[0] == '[' && content.EndsWith(']');
    }

    public IEnumerable<string> EnumerateLines() => EnumerateTagged().Select(t => t.Raw);

    /// <summary>
    /// 本文の行を、どのオブジェクトの何の行かと一緒に並べる。書き出しと行番号の計算はどちらもこれを使う。
    /// グループのかたまりの最初のオブジェクトでは説明コメントの前（先頭の空行の後ろ）に開始行を、
    /// 最後のオブジェクトでは本文の後ろ（末尾の空行の前）に終了行を出す。
    /// </summary>
    public IEnumerable<(string Raw, int Section, LineRole Role)> EnumerateTagged()
    {
        yield return (HeaderLine, -1, LineRole.FileHeader);
        foreach (var c in CropSets)
        {
            yield return (CropSetLine(c), -1, LineRole.Preamble);
            if (c.DayLayers != null) yield return (CropLayersLine(c.Name, false, c.DayLayers), -1, LineRole.Preamble);
            if (c.NightLayers != null && c.Night != null) yield return (CropLayersLine(c.Name, true, c.NightLayers), -1, LineRole.Preamble);
        }
        foreach (var src in PsdSources)
        {
            if (src.Day.Length > 0) yield return (PsdFileLine(false, src.Day, src.Id), -1, LineRole.Preamble);
            if (src.Night != null) yield return (PsdFileLine(true, src.Night, src.Id), -1, LineRole.Preamble);
        }
        foreach (var v in Variations) yield return (VariationLine(v), -1, LineRole.Preamble);
        if (!string.IsNullOrWhiteSpace(RagIni)) yield return (RagLine(RagIni), -1, LineRole.Preamble);
        foreach (var l in Preamble) yield return (l.Raw, -1, LineRole.Preamble);
        for (int i = 0; i < Sections.Count; i++)
        {
            var s = Sections[i];
            // バリエーションの条件が前のオブジェクトと変われば、ここに「; @group 条件」（共通に戻るなら「; @end」）を書く
            bool mark = (i == 0 ? null : Sections[i - 1].VariationCond) != s.VariationCond;
            // 前のオブジェクトと共通でないグループはここで始まり、次のオブジェクトと共通でないグループはここで終わる（入れ子は外側から始め、内側から終える）
            static int Common(List<string> a, List<string> b) { int c = 0; while (c < a.Count && c < b.Count && a[c] == b[c]) c++; return c; }
            int beginFrom = i == 0 ? 0 : Common(Sections[i - 1].Groups, s.Groups);
            int endFrom = i == Sections.Count - 1 ? 0 : Common(s.Groups, Sections[i + 1].Groups);
            bool begins = beginFrom < s.Groups.Count;
            bool ends = endFrom < s.Groups.Count;

            int lead = 0;
            if (begins || mark) while (lead < s.Leading.Count && s.Leading[lead].Kind == PanelLineKind.Blank) lead++;
            for (int k = 0; k < lead; k++) yield return (s.Leading[k].Raw, i, LineRole.Leading);
            if (mark) yield return (VariationMarkLine(s.VariationCond), i, LineRole.VariationMark);
            for (int g = beginFrom; g < s.Groups.Count; g++) yield return (GroupBeginLine(s.Groups[g]), i, LineRole.GroupBegin);
            for (int k = lead; k < s.Leading.Count; k++) yield return (s.Leading[k].Raw, i, LineRole.Leading);

            // 無効にしたオブジェクトは、見出しと中身の行の頭に「;」を付ける（中の空行は「;」だけ）
            string Out(PanelLine l) => !s.Disabled ? l.Raw : l.Kind == PanelLineKind.Blank && l.Raw.Length == 0 ? ";" : ";" + l.Raw;
            yield return (s.Disabled ? ";" + s.HeaderRaw : s.HeaderRaw, i, LineRole.SectionHeader);
            if (s.IsMainPanel) yield return (MainPanelLine, i, LineRole.Body);
            if (s.PsdLayers.Count > 0) yield return (PsdLayersLine(s.PsdLayers, s.PsdSource), i, LineRole.Body);
            if (s.CropSet != null) yield return (CropLine(s.CropSet), i, LineRole.Body);
            foreach (var c in s.CropShapes) yield return (CropShapeLine(c), i, LineRole.Body);

            int tail = s.Lines.Count;
            if (ends) while (tail > 0 && s.Lines[tail - 1].Kind == PanelLineKind.Blank) tail--;
            for (int k = 0; k < tail; k++) yield return (Out(s.Lines[k]), i, LineRole.Body);
            for (int g = s.Groups.Count - 1; g >= endFrom; g--) yield return (GroupEndLine(s.Groups[g]), i, LineRole.GroupEnd);
            for (int k = tail; k < s.Lines.Count; k++) yield return (Out(s.Lines[k]), i, LineRole.Body);
            foreach (var l in s.After) yield return (l.Raw, i, LineRole.Body);
            // 最後のオブジェクトが条件付きなら、最後に「; @end」で閉じる
            if (i == Sections.Count - 1 && s.VariationCond != null) yield return (VariationMarkLine(null), i, LineRole.VariationMark);
        }
    }

    public string Serialize()
    {
        var body = string.Join(NewLine, EnumerateLines());
        return EndsWithNewLine ? body + NewLine : body;
    }

    public List<SectionLineRange> LineMap()
    {
        var first = new int[Sections.Count];
        var header = new int[Sections.Count];
        var last = new int[Sections.Count];
        int line = 0;
        foreach (var (_, sec, role) in EnumerateTagged())
        {
            line++;
            if (sec < 0) continue;
            if (first[sec] == 0) first[sec] = line;
            if (role == LineRole.SectionHeader) header[sec] = line;
            last[sec] = line;
        }
        var map = new List<SectionLineRange>(Sections.Count);
        for (int i = 0; i < Sections.Count; i++) map.Add(new SectionLineRange(first[i], header[i], last[i]));
        return map;
    }

    /// <summary>本文の行番号（1 始まり）を含むセクションの番号。なければ -1。</summary>
    public int SectionAtLine(int line)
    {
        var map = LineMap();
        for (int i = 0; i < map.Count; i++)
            if (line >= map[i].FirstLine && line <= map[i].LastLine) return i;
        return -1;
    }

    /// <summary>新しいパネル。imageRelPath が null か空なら、[This] に画像を書かない（BVE は [This] の画像がなくても読める）。</summary>
    public static PanelDocument CreateNew(string? imageRelPath, int width, int height, string header = DefaultHeader)
    {
        var lines = new List<string>
        {
            header,
            "",
            "[This]",
            $"Resolution = {width}",
            "Left = 0",
            "Top = 0",
            $"Right = {width}",
            $"Bottom = {height}",
        };
        if (!string.IsNullOrWhiteSpace(imageRelPath)) lines.Add($"DaytimeImage = {imageRelPath}");
        lines.Add($"Center = {width / 2}, {height / 2}");
        lines.Add($"Origin = {width / 2}, {height / 2}");
        lines.Add("");
        return Parse(string.Join("\r\n", lines));
    }
}
