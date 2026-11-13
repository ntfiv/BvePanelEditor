using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BvePanelEditor.Core;

/// <summary>
/// RealAnalogGauge（BVE Workshop 作の ATS プラグイン。MIT ライセンス）の設定の読み書きと、プラグインと同じ計算。
/// <para>
/// プラグインの決まり（ソースの RealAnalogGauge.cpp・UnequalIntervalScale.cpp・NeedleVibration.cpp に合わせる）:
/// <list type="bullet">
/// <item>DLL と同じ名前の ini（親）の [FileName] Gauge0〜Gauge15 に、針ごとの設定の名前（拡張子なし、DLL のフォルダからの相対）を書く。</item>
/// <item>針ごとの ini: [SubjectKey] Subject（kmph・bc・mr・sap・bp・er・am・amabs・panelN・soundN）、[PanelIndex] Needle（出力先の ats 番号）、[Vibration] Period・Amplitude・Threshold。</item>
/// <item>針ごとの csv: 「入力値, 角度」の行。角度は 12 時が 0 で時計回りが正。間は直線でつなぎ、表の外は端の 2 点を延ばす。</item>
/// <item>出力: panel[Needle] = (角度 + 180) × 1000（整数）。パネルの針は InitialAngle = -180・LastAngle = 180・Minimum = 0・Maximum = 360000 にする。</item>
/// </list>
/// </para>
/// </summary>
public sealed class RagPoint
{
    public double Value { get; set; }
    public double Angle { get; set; }

    public RagPoint() { }
    public RagPoint(double value, double angle) { Value = value; Angle = angle; }
}

/// <summary>RealAnalogGauge の針 1 本の設定（針ごとの ini と csv）。</summary>
public sealed class RagGauge
{
    /// <summary>親 ini の GaugeN に書いた名前（拡張子なし）。親 ini に直接書いた針（互換用）は ""。</summary>
    public string Name { get; set; } = "";

    /// <summary>親 ini の GaugeN の N（0〜15）。親 ini に直接書いた針は -1。</summary>
    public int Slot { get; set; } = -1;

    public string IniPath { get; set; } = "";
    public string CsvPath { get; set; } = "";

    /// <summary>入力（[SubjectKey] Subject。小文字）。</summary>
    public string Subject { get; set; } = "kmph";

    /// <summary>出力先の ats 番号（[PanelIndex] Needle。0〜255 以外なら出力しない）。</summary>
    public int Needle { get; set; } = -1;

    public double Period { get; set; }
    public double Amplitude { get; set; }
    public double Threshold { get; set; }

    /// <summary>[Ellipse] Division（2 以上なら試作中の楕円形の計器。パネルくんでは動きを再現しない）。</summary>
    public int EllipseDivision { get; set; }

    /// <summary>csv の表（入力値の小さい順。同じ入力値は後の行が勝つ）。</summary>
    public List<RagPoint> Points { get; } = [];

    /// <summary>パネルくんで変えて、まだファイルに書いていない。</summary>
    public bool Dirty { get; set; }

    /// <summary>出力先が BVE の ats（0〜255）か。</summary>
    public bool HasOutput => Needle is >= 0 and <= 255 && EllipseDivision < 2;

    /// <summary>表示用の名前。</summary>
    public string Label => Slot >= 0 ? $"Gauge{Slot}: {Name}" : "親の設定ファイルに書いた針";

    /// <summary>表を入力値の順に並べ、同じ入力値は後のものだけ残す（プラグインの std::map と同じ）。</summary>
    public void Normalize()
    {
        var map = new SortedDictionary<double, double>();
        foreach (var p in Points) map[p.Value] = p.Angle;
        Points.Clear();
        foreach (var (v, a) in map) Points.Add(new RagPoint(v, a));
    }

    /// <summary>入力値から角度（プラグインの linearInterpolate と同じ。表が 2 行未満なら 0）。</summary>
    public double Interpolate(double x)
    {
        var pts = Points.GroupBy(p => p.Value).Select(g => g.Last()).OrderBy(p => p.Value).ToList();
        if (pts.Count < 2) return 0;
        RagPoint a, b;
        if (x < pts[0].Value) { a = pts[0]; b = pts[1]; }
        else if (x >= pts[^1].Value) { a = pts[^2]; b = pts[^1]; }
        else
        {
            int i = pts.FindIndex(p => p.Value > x);
            a = pts[i - 1]; b = pts[i];
        }
        return a.Angle + (x - a.Value) * (b.Angle - a.Angle) / (b.Value - a.Value);
    }

    /// <summary>低速の振動（プラグインの NeedleVibrationMain と同じ）。location は走った距離 m。</summary>
    public double Vibrate(double value, double location)
    {
        double coefficient = Period == 0 ? 0 : Math.Abs(2 * Math.PI / (10.0 * 1000 / 3600 * Period));
        double amplitude = Math.Abs(Amplitude), threshold = Math.Abs(Threshold);
        if (amplitude == 0 || coefficient == 0) return value;
        double speed = Math.Abs(value);
        if (threshold != 0 && threshold < speed) return speed;
        return speed + Math.Sin(location * coefficient) * (amplitude > speed ? speed : amplitude);
    }

    /// <summary>入力値（と走った距離）から、針の角度。</summary>
    public double OutputAngle(double input, double location) => Interpolate(Vibrate(input, location));

    /// <summary>入力値（と走った距離）から、ats に書く値（(角度 + 180) × 1000 を整数に。C の int への変換と同じく 0 の方へ切り捨て）。</summary>
    public double PanelValue(double input, double location) => Math.Truncate((OutputAngle(input, location) + 180) * 1000);

    /// <summary>プラグインの Subject を、パネルくんの状態量のキーに（panelN → atsN。soundN は null）。</summary>
    public static string? StateKey(string subject)
    {
        var s = subject.Trim().ToLowerInvariant();
        var m = Regex.Match(s, @"^panel(\d+)$");
        if (m.Success) return int.TryParse(m.Groups[1].Value, out var n) && n <= 255 ? $"ats{n}" : null;
        if (s.StartsWith("sound", StringComparison.Ordinal)) return null;
        return s is "kmph" or "bc" or "mr" or "sap" or "bp" or "er" or "am" or "amabs" ? s : null;
    }

    /// <summary>パネルの Subject を、プラグインの Subject に（atsN → panelN。使えないものは null）。</summary>
    public static string? FromPanelSubject(string panelSubject)
    {
        var s = panelSubject.Trim().ToLowerInvariant();
        if (Subjects.IsAts(s, out var n)) return $"panel{n}";
        return s is "kmph" or "bc" or "mr" or "sap" or "bp" or "er" or "am" or "amabs" ? s : null;
    }

    /// <summary>csv の中身を書く（「入力値,角度」の行）。</summary>
    public string CsvText()
    {
        var sb = new StringBuilder();
        foreach (var p in Points) sb.Append(Num(p.Value)).Append(',').Append(Num(p.Angle)).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>csv を読む（数として読めない行は飛ばす。プラグインと同じ）。</summary>
    public static List<RagPoint> ParseCsv(string text)
    {
        var list = new List<RagPoint>();
        foreach (var line in BveText.SplitLines(text))
        {
            var cols = line.Split(',');
            if (cols.Length < 2) continue;
            if (!TryNum(cols[0], out var v) || !TryNum(cols[1], out var a)) continue;
            list.Add(new RagPoint(v, a));
        }
        return list;
    }

    /// <summary>
    /// 表を直線にしたときの、端の 2 点（最初と最後の行）と、途中でいちばんずれる角度（度）。
    /// 表が 2 行未満なら null。
    /// </summary>
    public (RagPoint First, RagPoint Last, double MaxError, double ErrorAt)? LinearFit()
    {
        var pts = Points.GroupBy(p => p.Value).Select(g => g.Last()).OrderBy(p => p.Value).ToList();
        if (pts.Count < 2) return null;
        var f = pts[0];
        var l = pts[^1];
        double err = 0, at = f.Value;
        foreach (var p in pts)
        {
            double lin = f.Angle + (p.Value - f.Value) * (l.Angle - f.Angle) / (l.Value - f.Value);
            if (Math.Abs(p.Angle - lin) > err) { err = Math.Abs(p.Angle - lin); at = p.Value; }
        }
        return (f, l, err, at);
    }

    internal static string Num(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    internal static bool TryNum(string s, out double v) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}

/// <summary>RealAnalogGauge の設定一式（親 ini と、そこから読む針ごとの ini・csv）。</summary>
public sealed class RagConfig
{
    /// <summary>パネルの針を RealAnalogGauge 用にするときの角度の決まり。</summary>
    public const double InitialAngle = -180, LastAngle = 180, Minimum = 0, Maximum = 360000;

    /// <summary>親 ini（DLL と同じ名前の ini）。</summary>
    public string IniPath { get; }
    public string Dir => Path.GetDirectoryName(IniPath) ?? "";

    /// <summary>親 ini の Gauge0〜Gauge15（なければ null）。</summary>
    public string?[] Slots { get; } = new string?[16];

    public List<RagGauge> Gauges { get; } = [];

    /// <summary>読み込みで気づいたこと（ファイルがない・楕円形など）。</summary>
    public List<string> Notes { get; } = [];

    /// <summary>開発者モード（[DeveloperMode] Enabled）が有効のまま。</summary>
    public bool DeveloperMode { get; private set; }

    private RagConfig(string iniPath) => IniPath = Path.GetFullPath(iniPath);

    /// <summary>親 ini を読む（ない・読めなければ例外）。</summary>
    public static RagConfig Load(string iniPath)
    {
        var cfg = new RagConfig(iniPath);
        var (text, _) = BveText.Read(cfg.IniPath);
        var ini = Ini.Parse(text);
        for (int i = 0; i < 16; i++)
        {
            var name = Ini.Get(ini, "FileName", $"Gauge{i}");
            cfg.Slots[i] = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        cfg.DeveloperMode = Ini.Get(ini, "DeveloperMode", "Enabled") is { } dm && int.TryParse(dm.Trim(), out var d) && d != 0;

        // 親 ini に直接書いた針（互換用。csv は DLL と同じ名前）
        if (Ini.Get(ini, "PanelIndex", "Needle") is { } pn && int.TryParse(pn.Trim(), out var pni) && pni >= 0)
        {
            var g = new RagGauge { Slot = -1, IniPath = cfg.IniPath, CsvPath = Path.ChangeExtension(cfg.IniPath, ".csv") };
            ReadGauge(g, ini, cfg.Notes);
            cfg.Gauges.Add(g);
        }
        for (int i = 0; i < 16; i++)
        {
            if (cfg.Slots[i] is not { } name) continue;
            var basePath = Path.Combine(cfg.Dir, name);
            var g = new RagGauge { Name = name, Slot = i, IniPath = basePath + ".ini", CsvPath = basePath + ".csv" };
            if (!File.Exists(g.IniPath))
            {
                cfg.Notes.Add($"Gauge{i}（{name}）の設定ファイル {Path.GetFileName(g.IniPath)} がありません（プラグインはこの針を動かしません）");
                continue;
            }
            ReadGauge(g, Ini.Parse(BveText.Read(g.IniPath).Text), cfg.Notes);
            cfg.Gauges.Add(g);
        }
        return cfg;
    }

    private static void ReadGauge(RagGauge g, Dictionary<string, Dictionary<string, string>> ini, List<string> notes)
    {
        g.Subject = (Ini.Get(ini, "SubjectKey", "Subject") ?? "kmph").Trim().ToLowerInvariant();
        g.Needle = Ini.Get(ini, "PanelIndex", "Needle") is { } n && int.TryParse(n.Trim(), out var ni) ? ni : -1;
        g.Period = Dbl(Ini.Get(ini, "Vibration", "Period"));
        g.Amplitude = Dbl(Ini.Get(ini, "Vibration", "Amplitude"));
        g.Threshold = Dbl(Ini.Get(ini, "Vibration", "Threshold"));
        g.EllipseDivision = Ini.Get(ini, "Ellipse", "Division") is { } ed && int.TryParse(ed.Trim(), out var edi) ? edi : 0;
        if (File.Exists(g.CsvPath)) g.Points.AddRange(RagGauge.ParseCsv(BveText.Read(g.CsvPath).Text));
        else notes.Add($"{g.Label} の表 {Path.GetFileName(g.CsvPath)} がありません（針は 0 度の向きのままになります）");
        g.Normalize();
        if (g.Points.Count == 1) notes.Add($"{g.Label} の表が 1 行しかありません（2 行以上必要です）");
        if (g.EllipseDivision >= 2) notes.Add($"{g.Label} は楕円形の計器（試作中の機能）なので、パネルくんでは動きを再現しません");
        if (StateKeyOrNull(g) == null) notes.Add($"{g.Label} の入力「{g.Subject}」は、パネルくんでは動かせません（sound の値など）");
        static double Dbl(string? s) => s != null && RagGauge.TryNum(s, out var v) ? v : 0;
    }

    private static string? StateKeyOrNull(RagGauge g) => RagGauge.StateKey(g.Subject);

    /// <summary>ats 番号に出力する針（なければ null。2 本以上あれば後のもの＝プラグインで最後に書くもの）。</summary>
    public RagGauge? ForAts(int index) => Gauges.LastOrDefault(g => g.HasOutput && g.Needle == index);

    /// <summary>空いている GaugeN の N（なければ -1）。</summary>
    public int FreeSlot() => Array.FindIndex(Slots, s => s == null);

    /// <summary>針ごとのファイルの名前に使える、まだない名前。</summary>
    public string UniqueName(string wish)
    {
        var name = Regex.Replace(wish.Trim(), @"[\\/:*?""<>|\s]+", "_").Trim('_', '.');
        if (name.Length == 0) name = "Gauge";
        string Candidate(int k) => k == 0 ? name : $"{name}_{k + 1}";
        for (int k = 0; ; k++)
        {
            var c = Candidate(k);
            if (Slots.Any(s => s != null && s.Equals(c, StringComparison.OrdinalIgnoreCase))) continue;
            if (File.Exists(Path.Combine(Dir, c + ".ini")) || File.Exists(Path.Combine(Dir, c + ".csv"))) continue;
            return c;
        }
    }

    /// <summary>新しい針ごとの ini の中身。</summary>
    public static string NewGaugeIni(string subject, int needle) =>
        $"[SubjectKey]\r\nSubject = {subject}\r\n\r\n[PanelIndex]\r\nNeedle = {needle}\r\n";

    /// <summary>親 ini の中身に GaugeN = name を足した（消した）もの。</summary>
    public static string SetSlotLine(string iniText, int slot, string? name) =>
        name == null ? Ini.Remove(iniText, "FileName", $"Gauge{slot}") : Ini.Set(iniText, "FileName", $"Gauge{slot}", name);

    /// <summary>
    /// 書いてよいかを確かめずに、テキストをファイルに書く。前の中身があれば、最初に書くときだけ .bak を残す。
    /// 文字コードは、前のファイルがあればそれに合わせ、なければ Shift_JIS（プラグインは ANSI で読む）。
    /// </summary>
    public static void WriteText(string path, string text, HashSet<string>? backedUp = null)
    {
        Encoding enc = BveText.ShiftJis;
        bool bom = false;
        if (File.Exists(path))
        {
            var (_, info) = BveText.Read(path);
            enc = info.Encoding;
            bom = info.HasBom;
            if (backedUp == null || backedUp.Add(Path.GetFullPath(path)))
                if (!File.Exists(path + ".bak")) File.Copy(path, path + ".bak");
        }
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, BveText.Encode(text, enc, bom));
    }

    /// <summary>針ごとの ini の振動・入力・出力の値を、中身に書き込んだもの（ほかの行はそのまま）。</summary>
    public static string UpdateGaugeIni(string iniText, RagGauge g)
    {
        var t = Ini.Set(iniText, "SubjectKey", "Subject", g.Subject);
        t = Ini.Set(t, "PanelIndex", "Needle", g.Needle.ToString(CultureInfo.InvariantCulture));
        bool vib = g.Period != 0 || g.Amplitude != 0 || g.Threshold != 0 || Ini.Get(Ini.Parse(iniText), "Vibration", "Period") != null;
        if (vib)
        {
            t = Ini.Set(t, "Vibration", "Period", RagGauge.Num(g.Period));
            t = Ini.Set(t, "Vibration", "Amplitude", RagGauge.Num(g.Amplitude));
            t = Ini.Set(t, "Vibration", "Threshold", RagGauge.Num(g.Threshold));
        }
        return t;
    }
}

/// <summary>Windows の ini（GetPrivateProfileString と同じく、セクション名・キーの大文字小文字を区別しない）の小さな読み書き。</summary>
public static class Ini
{
    public static Dictionary<string, Dictionary<string, string>> Parse(string text)
    {
        var d = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? cur = null;
        foreach (var raw in BveText.SplitLines(text))
        {
            var l = raw.Trim();
            if (l.Length == 0 || l[0] == ';') continue;
            if (l[0] == '[' && l.IndexOf(']') is var e and > 0)
            {
                var name = l[1..e].Trim();
                if (!d.TryGetValue(name, out cur)) d[name] = cur = new(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            int eq = l.IndexOf('=');
            if (cur == null || eq <= 0) continue;
            var key = l[..eq].Trim();
            if (!cur.ContainsKey(key)) cur[key] = l[(eq + 1)..].Trim(); // 同じキーは最初のものが使われる
        }
        return d;
    }

    public static string? Get(Dictionary<string, Dictionary<string, string>> ini, string section, string key) =>
        ini.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) ? v : null;

    /// <summary>キーの値を書き換える（なければセクションの最後に足す。セクションもなければ最後に足す）。ほかの行はそのまま。</summary>
    public static string Set(string text, string section, string key, string value)
    {
        string nl = text.Contains("\r\n") ? "\r\n" : text.Contains('\n') ? "\n" : "\r\n";
        var lines = BveText.SplitLines(text).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0 && text.Length > 0) lines.RemoveAt(lines.Count - 1);
        int start = -1, end = lines.Count;
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i].Trim();
            if (l.StartsWith('[') && l.IndexOf(']') is var e and > 0)
            {
                if (start >= 0) { end = i; break; }
                if (l[1..e].Trim().Equals(section, StringComparison.OrdinalIgnoreCase)) start = i;
            }
        }
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add($"{key} = {value}");
        }
        else
        {
            int found = -1;
            for (int i = start + 1; i < end; i++)
            {
                var l = lines[i].Trim();
                int eq = l.IndexOf('=');
                if (l.StartsWith(';') || eq <= 0) continue;
                if (l[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) { found = i; break; }
            }
            if (found >= 0)
            {
                var old = lines[found];
                int eq = old.IndexOf('=');
                // 「Key = 値」の = の後ろの空白の付け方は元のまま
                int vs = eq + 1;
                while (vs < old.Length && old[vs] is ' ' or '\t') vs++;
                lines[found] = old[..vs] + value;
            }
            else
            {
                int at = end;
                while (at - 1 > start && lines[at - 1].Trim().Length == 0) at--; // セクションの後ろの空行より前に入れる
                lines.Insert(at, $"{key} = {value}");
            }
        }
        return string.Join(nl, lines) + nl;
    }

    /// <summary>キーの行を消す（なければそのまま）。</summary>
    public static string Remove(string text, string section, string key)
    {
        string nl = text.Contains("\r\n") ? "\r\n" : text.Contains('\n') ? "\n" : "\r\n";
        var lines = BveText.SplitLines(text).ToList();
        bool inSec = false;
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i].Trim();
            if (l.StartsWith('[') && l.IndexOf(']') is var e and > 0) { inSec = l[1..e].Trim().Equals(section, StringComparison.OrdinalIgnoreCase); continue; }
            int eq = l.IndexOf('=');
            if (inSec && !l.StartsWith(';') && eq > 0 && l[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                lines.RemoveAt(i);
                return string.Join(nl, lines);
            }
        }
        return text;
    }
}

/// <summary>車両の ATS プラグインの設定から、RealAnalogGauge の親 ini を探す。</summary>
public static partial class RagLocator
{
    /// <summary>
    /// 見つかった RealAnalogGauge の親 ini（あるものだけ。見つけた順）。
    /// ATS が RealAnalogGauge の DLL そのものか、PluginOrganizer（Instructions*.yml）・DetailManager（detailmodules.txt）などの
    /// テキストに書いた RealAnalogGauge の DLL をたどる。見つからなければ、車両のフォルダの下（深さ 6 まで）から探す。
    /// </summary>
    public static List<string> Find(string? vehiclePath, IEnumerable<string?> atsPaths)
    {
        var found = new List<string>();
        void AddDll(string dll)
        {
            var ini = Path.ChangeExtension(Path.GetFullPath(dll), ".ini");
            if (File.Exists(ini) && IsParentIni(ini) && !found.Contains(ini, StringComparer.OrdinalIgnoreCase)) found.Add(ini);
        }
        foreach (var ats in atsPaths.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (Path.GetFileNameWithoutExtension(ats).Contains("RealAnalogGauge", StringComparison.OrdinalIgnoreCase)) AddDll(ats);
                var dir = Path.GetDirectoryName(ats);
                if (dir == null || !Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir).Where(f => f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                             || f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
                {
                    if (new FileInfo(f).Length > 1_000_000) continue;
                    var text = BveText.Read(f).Text;
                    foreach (Match m in DllRegex().Matches(text))
                    {
                        var p = m.Value.Trim().Trim('"', '\'');
                        if (!Path.GetFileName(p).Contains("RealAnalogGauge", StringComparison.OrdinalIgnoreCase)) continue;
                        try { AddDll(Path.IsPathRooted(p) ? p : Path.Combine(dir, p)); } catch (ArgumentException) { }
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (found.Count == 0 && vehiclePath != null && Path.GetDirectoryName(Path.GetFullPath(vehiclePath)) is { } vdir)
            foreach (var ini in Search(vdir, 6))
                if (!found.Contains(ini, StringComparer.OrdinalIgnoreCase)) found.Add(ini);
        return found;
    }

    /// <summary>[FileName] の Gauge0〜15 か [PanelIndex] があり、DLL と同じ名前の ini（RealAnalogGauge の親 ini）か。</summary>
    public static bool IsParentIni(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 200_000) return false;
            var ini = Ini.Parse(BveText.Read(path).Text);
            return ini.ContainsKey("FileName") || (ini.ContainsKey("PanelIndex") && ini.ContainsKey("SubjectKey") && ini.ContainsKey("KeyIndex"));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static IEnumerable<string> Search(string dir, int depth)
    {
        IEnumerable<string> files = [], dirs = [];
        try
        {
            files = Directory.EnumerateFiles(dir, "RealAnalogGauge*.ini").ToList();
            dirs = depth > 0 ? Directory.EnumerateDirectories(dir).ToList() : [];
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        foreach (var f in files) if (IsParentIni(f)) yield return Path.GetFullPath(f);
        foreach (var d in dirs)
            foreach (var f in Search(d, depth - 1)) yield return f;
    }

    [GeneratedRegex(@"(?:[A-Za-z]:)?[^\s""'|:*?<>]+\.dll", RegexOptions.IgnoreCase)]
    private static partial Regex DllRegex();
}
