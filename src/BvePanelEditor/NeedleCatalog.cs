using System.IO;

namespace BvePanelEditor;

/// <summary>指針 1 本の置き方（画像・回転の中心・角度・値の範囲など）。</summary>
public sealed class NeedleSpec
{
    public string Title = "";
    /// <summary>昼の画像（フルパス）。</summary>
    public string DayFile = "";
    /// <summary>夜の画像（フルパス、なければ null）。</summary>
    public string? NightFile;
    public int OriginX, OriginY;
    public double Radius = 60, InitialAngle = -125, LastAngle = 125, Minimum, Maximum = 160;
    public string Subject = "kmph";
    /// <summary>針の動き（ばね）。0 ならすぐ追いつく（キーを書かない）。</summary>
    public double NaturalFreq, DampingRatio;
    /// <summary>置く位置のずれ（影の針など。組の基準の位置から）。</summary>
    public int OffsetX, OffsetY;
    /// <summary>透過色（"000000" など。なければ書かない）。</summary>
    public string? TransparentColor;
    /// <summary>最小・最大で針を止める（BVE 5.8 以降・6 で使えるキー）。</summary>
    public bool StopPin;

    public NeedleSpec Clone() => (NeedleSpec)MemberwiseClone();
}

/// <summary>指針を置く画面の一覧の 1 行（1 本の針か、同じ位置に置く針の組）。</summary>
public sealed class NeedlePreset
{
    public required string Label;
    public required List<NeedleSpec> Needles;   // 時計は 3 本
    public string Note = "";
    /// <summary>標準の針のうち、どの版で足したものか（設定ファイルに後から書き足すため）。</summary>
    public int Since = 1;

    public NeedlePreset Clone() => new() { Label = Label, Note = Note, Needles = Needles.Select(n => n.Clone()).ToList() };
}

public static class NeedleCatalog
{
    /// <summary>標準の針のフォルダ（exe と同じ場所の needles。free_needle の CC0 の針）。</summary>
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "needles");

    /// <summary>E217 の針のフォルダ（exe と同じ場所の needle_E217。内房線製作チームの素材なので needles と分けて置く）。</summary>
    public static string E217Folder => Path.Combine(AppContext.BaseDirectory, "needle_E217");

    private static NeedleSpec N(string title, string file, int ox, int oy, double radius, double a0, double a1, string subject, double min, double max, double freq = 0, double damp = 0) => new()
    {
        Title = title, DayFile = Path.Combine(Folder, file + ".png"), NightFile = Path.Combine(Folder, file + "_dark.png"),
        OriginX = ox, OriginY = oy, Radius = radius, InitialAngle = a0, LastAngle = a1, Subject = subject, Minimum = min, Maximum = max,
        NaturalFreq = freq, DampingRatio = damp,
    };

    // 速度計は 固有角振動数 6.3・減衰比 0.5、圧力計は 80・0.1
    private static NeedleSpec Black() => N("圧力計の針（黒）", "nd_prs_up", 32, 164, 61, -135, 135, "bc", 0, 1500, 80, 0.1);
    private static NeedleSpec RedA() => N("圧力計の針（赤）", "nd_prs1_low", 32, 164, 61, -135, 135, "mr", 0, 1500, 80, 0.1);
    // 赤Ｂタイプの画像は E217 の赤い針と同じもの（内房線製作チームの素材）なので、needle_E217 の画像を使う
    private static NeedleSpec RedB()
    {
        var n = N("圧力計の針（赤）", "nd_prs2_low", 64, 328, 61, -135, 135, "mr", 0, 1500, 80, 0.1);
        n.DayFile = Path.Combine(E217Folder, "needle_prs_low_E217.png");
        n.NightFile = Path.Combine(E217Folder, "needle_prs_low_n_E217.png");
        return n;
    }

    /// <summary>前の版で needles に置いていた画像のうち、E217 の針と同じもの（今は needle_E217 の画像を使う）。</summary>
    private static readonly Dictionary<string, string> MovedToE217 = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nd_prs2_low.png"] = "needle_prs_low_E217.png",
        ["nd_prs2_low_dark.png"] = "needle_prs_low_n_E217.png",
    };

    /// <summary>E217 の針（needle_E217 の画像。値は panel_E217.txt と同じ。Tilt は E217 のパネルの傾きなので入れない）。</summary>
    private static NeedleSpec E(string title, string day, string night, int ox, int oy, double radius, double a0, double a1, string subject, double min, double max,
        double freq, double damp, int dx = 0, int dy = 0, string? transparent = null) => new()
    {
        Title = title, DayFile = Path.Combine(E217Folder, day + "_E217.png"), NightFile = Path.Combine(E217Folder, night + "_E217.png"),
        OriginX = ox, OriginY = oy, Radius = radius, InitialAngle = a0, LastAngle = a1, Subject = subject, Minimum = min, Maximum = max,
        NaturalFreq = freq, DampingRatio = damp, StopPin = true, OffsetX = dx, OffsetY = dy, TransparentColor = transparent,
    };

    private static NeedleSpec E217Speed() => E("速度計(E217)", "needle_speed", "needle_speed_n", 8, 256, 180, -120, 120, "kmph", 0, 160, 6.3, 0.5);
    private static NeedleSpec E217SpeedShadow() => E("速度計の影(E217)", "needle_speed_shadow", "needle_speed_null", 16, 256, 180, -120, 120, "kmph", 0, 160, 6.3, 0.5, 6, 3);
    private static NeedleSpec E217Red(int dx = 0, int dy = 0) => E("圧力計の針（赤）(E217)", "needle_prs_low", "needle_prs_low_n", 64, 328, 170, -130.8, 136.5, "mr", 10, 1000, 80, 0.1, dx, dy);
    private static NeedleSpec E217Black(int dx = 0, int dy = 0) => E("圧力計の針（黒）(E217)", "needle_prs_up", "needle_prs_up_n", 32, 164, 170, -130.8, 136.5, "bc", 10, 1000, 80, 0.1, dx, dy, "000000");
    private static NeedleSpec E217PrsShadow(string title, string subject, int dx, int dy, string? transparent) =>
        E(title, "needle_prs_low_shadow", "needle_prs_null", 32, 164, 170, -130.8, 136.5, subject, 10, 1000, 80, 0.1, dx, dy, transparent);

    /// <summary>
    /// 一覧（指針を置く画面の左）。exe と同じフォルダの needles.txt に書く（メモ帳で書き換えられる形）。
    /// ファイルがなければ標準の針で作る。読めない行は LastWarnings に入れて、読める所だけ使う。
    /// </summary>
    public static List<NeedlePreset> Presets()
    {
        LastWarnings = [];
        try
        {
            if (!File.Exists(ConfigFile))
            {
                var builtin = Builtin();
                try { Save(builtin); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                return builtin;
            }
            var lines = File.ReadAllLines(ConfigFile).ToList();
            if (Upgrade(lines))
            {
                try { File.WriteAllLines(ConfigFile, lines, new System.Text.UTF8Encoding(true)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            var (list, warnings) = Parse(lines);
            LastWarnings = warnings;
            return list;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LastWarnings = [$"{ConfigFile} を読めませんでした（{e.Message}）。標準の針を出しています"];
            return Builtin();
        }
    }

    /// <summary>一覧のファイルを置くフォルダ（既定は exe と同じフォルダ。確認用の起動では一時フォルダ）。</summary>
    public static string StoreDir { get; set; } = AppContext.BaseDirectory;
    public static string ConfigFile => Path.Combine(StoreDir, "needles.txt");

    /// <summary>直前に読んだときの、読めなかった行など。</summary>
    public static List<string> LastWarnings { get; private set; } = [];

    /// <summary>標準の針の版。標準の針を足したら上げる（足した針の Since もこの値にする）。</summary>
    public const int BuiltinVersion = 2;
    private const string VersionKey = ";@標準の針の版";

    /// <summary>
    /// 設定ファイルの「;@標準の針の版」が今より古ければ、その後に足した標準の針を末尾に書き足して版を上げる
    /// （user が書いた行・コメント・並びはそのまま）。書き足したら true。
    /// </summary>
    public static bool Upgrade(List<string> lines)
    {
        int at = lines.FindIndex(l => l.TrimStart('\uFEFF').StartsWith(VersionKey));
        int version = 1;
        if (at >= 0 && lines[at].Split('=') is [_, var v] && int.TryParse(v.Trim(), out var n)) version = n;
        if (version >= BuiltinVersion) return false;
        var added = Builtin().Where(p => p.Since > version).ToList();
        if (at >= 0) lines[at] = $"{VersionKey} = {BuiltinVersion}";
        else lines.Insert(Math.Min(lines.Count, Header.Length), $"{VersionKey} = {BuiltinVersion}");
        if (added.Count > 0)
        {
            lines.Add("");
            lines.Add($"; ---- パネルくんの標準の針に足したもの（版 {BuiltinVersion}） ----");
            lines.AddRange(FormatBody(added));
        }
        return true;
    }

    private const string SetHeader = "[一覧]";
    private const string NeedleHeader = "[針]";

    private static readonly string[] Header =
    [
        "; パネルくん「指針を置く」の一覧（このファイルを書き換えると、一覧が変わります。画面の「設定ファイルを編集」でも開けます）",
        ";",
        "; ・[一覧] から次の [一覧] までが、一覧の 1 行（1 本の針か、同じ位置に置く針の組）。書いた順に並びます",
        ";     名前 = 一覧に出す名前（組なら、置くときにこの名前でグループにします）",
        ";     説明 = 選んだときに出す説明（なくてもよい）",
        "; ・[針] は、その行で置く針。2 つ以上書くと組になり、同じ位置に書いた順に重ねて置きます（あとに書いたものが上）",
        ";     名前 = 針の名前（パネルファイルの計器の名前になります）",
        ";     DaytimeImage・NighttimeImage = 画像。exe の needles フォルダからの場所（E217 の針は ..\\needle_E217\\…）か、フルパス（夜はなくてもよい）",
        ";     Origin・Radius・InitialAngle・LastAngle・Minimum・Maximum・Subject・NaturalFreq・DampingRatio は",
        ";     BVE のパネルファイルの [Needle] と同じ（NaturalFreq・DampingRatio は 0 か書かなければ、キーを書きません）",
        ";     TransparentColor・StopPin（True）も書けます（StopPin は BVE 5.8 以降・BVE6 で使えるキー）",
        ";     位置のずれ = X, Y … 組の中で、この針だけ置く位置をずらす（影の針など）",
        "; ・「;」から後ろはコメントです。このファイルを消すと、標準の針の一覧で作り直します",
        "; ・画面の ↑↓ で並べ替えると、このファイルを書き直します（自分で書いたコメントは消えます）",
    ];

    private static string F(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>画像のパス: exe のフォルダの中（needles・needle_E217）なら needles フォルダからの場所（..\needle_E217\… など）。外ならフルパス。</summary>
    private static string ImageText(string full)
    {
        var f = Path.GetFullPath(full);
        var dir = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return f.StartsWith(dir, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(Folder, f) : f;
    }

    private static string ImagePath(string text)
    {
        var t = text.Trim().Trim('"');
        if (Path.IsPathRooted(t)) return t;
        var full = Path.GetFullPath(Path.Combine(Folder, t));
        // 前の版は E217 の針も needles に置いていた。needles になく needle_E217 にあれば、そちらを使う
        if (!File.Exists(full) && Path.GetFileName(t) is { Length: > 0 } name)
        {
            if (MovedToE217.TryGetValue(name, out var moved)) name = moved;
            if (File.Exists(Path.Combine(E217Folder, name))) return Path.Combine(E217Folder, name);
        }
        return full;
    }

    /// <summary>一覧をファイルの形にする。</summary>
    public static List<string> Format(IEnumerable<NeedlePreset> presets)
    {
        var lines = new List<string>(Header) { $"{VersionKey} = {BuiltinVersion}" };
        lines.AddRange(FormatBody(presets));
        return lines;
    }

    private static List<string> FormatBody(IEnumerable<NeedlePreset> presets)
    {
        var lines = new List<string>();
        foreach (var p in presets)
        {
            lines.Add("");
            lines.Add(SetHeader);
            lines.Add($"名前 = {p.Label}");
            if (p.Note.Length > 0) lines.Add($"説明 = {p.Note}");
            foreach (var n in p.Needles)
            {
                lines.Add("");
                lines.Add(NeedleHeader);
                lines.Add($"名前 = {n.Title}");
                lines.Add($"DaytimeImage = {ImageText(n.DayFile)}");
                if (n.NightFile != null) lines.Add($"NighttimeImage = {ImageText(n.NightFile)}");
                lines.Add($"Origin = {n.OriginX}, {n.OriginY}");
                lines.Add($"Radius = {F(n.Radius)}");
                lines.Add($"InitialAngle = {F(n.InitialAngle)}");
                lines.Add($"LastAngle = {F(n.LastAngle)}");
                lines.Add($"Minimum = {F(n.Minimum)}");
                lines.Add($"Maximum = {F(n.Maximum)}");
                lines.Add($"Subject = {n.Subject}");
                if (n.NaturalFreq > 0) lines.Add($"NaturalFreq = {F(n.NaturalFreq)}");
                if (n.DampingRatio > 0) lines.Add($"DampingRatio = {F(n.DampingRatio)}");
                if (n.TransparentColor != null) lines.Add($"TransparentColor = {n.TransparentColor}");
                if (n.StopPin) lines.Add("StopPin = True");
                if (n.OffsetX != 0 || n.OffsetY != 0) lines.Add($"位置のずれ = {n.OffsetX}, {n.OffsetY}");
            }
        }
        return lines;
    }

    /// <summary>ファイルの行から一覧を読む。読めない行は warnings に「N 行目: …」で入れる。</summary>
    public static (List<NeedlePreset> Presets, List<string> Warnings) Parse(IReadOnlyList<string> lines)
    {
        var list = new List<NeedlePreset>();
        var warnings = new List<string>();
        NeedlePreset? set = null;
        NeedleSpec? needle = null;
        int setLine = 0;
        void EndSet()
        {
            if (set == null) return;
            set.Needles.RemoveAll(n => n.DayFile.Length == 0);
            if (set.Needles.Count == 0) warnings.Add($"{setLine} 行目: 「{set.Label}」に [針] がありません（一覧に出しません）");
            else list.Add(set);
            set = null;
        }
        for (int i = 0; i < lines.Count; i++)
        {
            int no = i + 1;
            var raw = lines[i];
            int c = raw.IndexOf(';');
            var t = (c >= 0 ? raw[..c] : raw).Trim().TrimStart('\uFEFF');
            if (t.Length == 0) continue;
            if (t == SetHeader)
            {
                if (needle is { DayFile.Length: 0 }) warnings.Add($"{no} 行目より前: DaytimeImage のない [針] があります（使いません）");
                EndSet();
                set = new NeedlePreset { Label = "", Needles = [] };
                needle = null;
                setLine = no;
                continue;
            }
            if (t == NeedleHeader)
            {
                if (needle is { DayFile.Length: 0 }) warnings.Add($"{no} 行目より前: DaytimeImage のない [針] があります（使いません）");
                if (set == null) { warnings.Add($"{no} 行目: [一覧] より前に [針] があります（1 本だけの行にします）"); set = new NeedlePreset { Label = "", Needles = [] }; setLine = no; }
                needle = new NeedleSpec { DayFile = "" };
                set.Needles.Add(needle);
                continue;
            }
            if (t.StartsWith('['))
            {
                warnings.Add($"{no} 行目: 「{t}」は使えません（[一覧] か [針]）");
                needle = null;
                continue;
            }
            int eq = t.IndexOf('=');
            if (eq <= 0) { warnings.Add($"{no} 行目: 「キー = 値」の形になっていません"); continue; }
            var key = t[..eq].Trim();
            var val = t[(eq + 1)..].Trim();
            if (needle == null)
            {
                if (set == null) { warnings.Add($"{no} 行目: [一覧] より前の「{key}」は使いません"); continue; }
                if (key == "名前") set.Label = val;
                else if (key == "説明") set.Note = val;
                else warnings.Add($"{no} 行目: [一覧] で使えないキー「{key}」です（名前・説明）");
                continue;
            }
            bool Num(out double v) => double.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);
            void Set(Action<double> apply) { if (Num(out var v)) apply(v); else warnings.Add($"{no} 行目: {key} の値「{val}」が数ではありません"); }
            switch (key.ToLowerInvariant())
            {
                case "名前": needle.Title = val; break;
                case "daytimeimage": needle.DayFile = val.Length > 0 ? ImagePath(val) : ""; break;
                case "nighttimeimage": needle.NightFile = val.Length > 0 ? ImagePath(val) : null; break;
                case "origin":
                    {
                        var xy = val.Split(',');
                        if (xy.Length == 2 && int.TryParse(xy[0].Trim(), out var x) && int.TryParse(xy[1].Trim(), out var y)) { needle.OriginX = x; needle.OriginY = y; }
                        else warnings.Add($"{no} 行目: Origin は「X, Y」（整数）で書いてください");
                        break;
                    }
                case "radius": Set(v => needle.Radius = v); break;
                case "initialangle": Set(v => needle.InitialAngle = v); break;
                case "lastangle": Set(v => needle.LastAngle = v); break;
                case "minimum": Set(v => needle.Minimum = v); break;
                case "maximum": Set(v => needle.Maximum = v); break;
                case "subject": needle.Subject = val; break;
                case "naturalfreq": Set(v => needle.NaturalFreq = v); break;
                case "dampingratio": Set(v => needle.DampingRatio = v); break;
                case "transparentcolor": needle.TransparentColor = val.Length > 0 ? val : null; break;
                case "stoppin":
                    if (bool.TryParse(val, out var pin)) needle.StopPin = pin;
                    else if (val == "1" || val == "0") needle.StopPin = val == "1";
                    else warnings.Add($"{no} 行目: StopPin は True か False で書いてください");
                    break;
                case "位置のずれ":
                    {
                        var xy = val.Split(',');
                        if (xy.Length == 2 && int.TryParse(xy[0].Trim(), out var ox) && int.TryParse(xy[1].Trim(), out var oy)) { needle.OffsetX = ox; needle.OffsetY = oy; }
                        else warnings.Add($"{no} 行目: 位置のずれ は「X, Y」（整数）で書いてください");
                        break;
                    }
                default: warnings.Add($"{no} 行目: [針] で使えないキー「{key}」です"); break;
            }
        }
        if (needle is { DayFile.Length: 0 }) warnings.Add("最後の [針] に DaytimeImage がありません（使いません）");
        EndSet();
        foreach (var n in list.SelectMany(p => p.Needles).Where(n => n.Title.Length == 0)) n.Title = Path.GetFileNameWithoutExtension(n.DayFile);
        foreach (var p in list.Where(p => p.Label.Length == 0)) p.Label = p.Needles[0].Title.Length > 0 ? p.Needles[0].Title : Path.GetFileNameWithoutExtension(p.Needles[0].DayFile);
        foreach (var p in list)
            foreach (var n in p.Needles.Where(n => !File.Exists(n.DayFile)))
                warnings.Add($"「{p.Label}」の画像「{ImageText(n.DayFile)}」がありません");
        return (list, warnings);
    }

    /// <summary>一覧をファイルに書く（UTF-8・BOM 付き。メモ帳で開ける形）。</summary>
    public static void Save(IEnumerable<NeedlePreset> presets)
    {
        Directory.CreateDirectory(StoreDir);
        File.WriteAllLines(ConfigFile, Format(presets), new System.Text.UTF8Encoding(true));
    }

    /// <summary>標準の針（一緒に配る free_needle の針）。回転の中心などは、作者の既存のパネルで使っている値。</summary>
    public static List<NeedlePreset> Builtin() =>
    [
        new() { Label = "速度計の針Ａタイプ", Needles = [N("速度計", "nd_speed", 16, 225, 60, -125, 125, "kmph", 0, 160, 6.3, 0.5)] },
        new() { Label = "速度計の針Ｂタイプ", Needles = [N("速度計", "nd_speed_dc", 16, 225, 60, -125, 125, "kmph", 0, 160, 6.3, 0.5)] },
        new()
        {
            Label = "速度計の針Ｃタイプ", Since = 2,
            Needles = [new() { Title = "速度計", DayFile = Path.Combine(Folder, "speed_toyo.png"), NightFile = Path.Combine(Folder, "speed_toyo_dark.png"),
                OriginX = 32, OriginY = 252, Radius = 248, InitialAngle = -125, LastAngle = 125, Subject = "kmph", Minimum = 0, Maximum = 160, NaturalFreq = 6.3, DampingRatio = 0.5 }],
        },
        new() { Label = "圧力計の針（黒）", Needles = [Black()] },
        new() { Label = "圧力計の針（赤）Ａタイプ", Needles = [RedA()], Note = "軸の丸がない針です。圧力計の針（黒）と同じ位置に置いて、黒い針の軸で隠します" },
        new() { Label = "圧力計の針（赤）Ｂタイプ", Needles = [RedB()] },
        // 組み合わせ: 赤（元空気だめ）を下、黒（ブレーキシリンダ）を上に、同じ位置に置く
        new() { Label = "圧力計Ａ", Needles = [RedA(), Black()], Note = "圧力計の針（黒）＋圧力計の針（赤）Ａタイプ。2 本を同じ位置に置きます（赤が下、黒が上）" },
        new() { Label = "圧力計Ｂ", Needles = [RedB(), Black()], Note = "圧力計の針（黒）＋圧力計の針（赤）Ｂタイプ。2 本を同じ位置に置きます（赤が下、黒が上）" },
        // E217 同梱の針（panel_E217.txt と同じ値・並び。影の針は少しずらして下に置く）
        new() { Label = "速度計(E217)", Since = 2, Needles = [E217SpeedShadow(), E217Speed()], Note = "E217 の速度計の針と影。影は右下に少しずらして下に置きます（夜は影を出しません）" },
        new()
        {
            Label = "圧力計(E217)", Since = 2,
            Needles = [E217PrsShadow("圧力計の針（赤）の影(E217)", "mr", 7, 7, null), E217PrsShadow("圧力計の針（黒）の影(E217)", "bc", 11, 11, "000000"), E217Red(), E217Black(-3, -3)],
            Note = "E217 の圧力計（赤・黒）と、それぞれの影。panel_E217.txt と同じ位置関係で置きます（影が下、赤、黒の順に上）",
        },
        new() { Label = "圧力計の針（赤）(E217)", Since = 2, Needles = [E217Red()] },
        new() { Label = "圧力計の針（黒）(E217)", Since = 2, Needles = [E217Black()] },
        new()
        {
            Label = "時計（時・分・秒の 3 本）", Note = "3 本を同じ位置に置きます",
            Needles =
            [
                N("時計 時針", "nd_clock_hour", 32, 165, 30, 0, 360, "hour", 0, 12),
                N("時計 分針", "nd_clock_min", 32, 165, 30, 0, 360, "min", 0, 60),
                N("時計 秒針", "nd_clock_sec", 32, 165, 30, 0, 360, "sec", 0, 60),
            ],
        },
        new() { Label = "時計の時針", Needles = [N("時計 時針", "nd_clock_hour", 32, 165, 30, 0, 360, "hour", 0, 12)] },
        new() { Label = "時計の分針", Needles = [N("時計 分針", "nd_clock_min", 32, 165, 30, 0, 360, "min", 0, 60)] },
        new() { Label = "時計の秒針", Needles = [N("時計 秒針", "nd_clock_sec", 32, 165, 30, 0, 360, "sec", 0, 60)] },
    ];
}
