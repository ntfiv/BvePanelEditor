using System.Text.RegularExpressions;

namespace BvePanelEditor.Core;

/// <summary>画像のファイル名の決まり: 夜の画像（_N・night・dark など）と、連番（ID_motor_0・01・001 など）。</summary>
public static partial class ImageNames
{
    /// <summary>夜の画像に付ける符号（名前の後ろ、または連番の数の前）。</summary>
    private static readonly string[] NightMarks = ["_dark", "_Dark", "dark", "_N", "N", "_n", "-N", "_night", "_Night", "night", "Night", "-night", "_夜", "夜"];

    [GeneratedRegex(@"^(?<pre>.*?)(?<num>\d+)(?<post>\D*)$")]
    private static partial Regex NumberRegex();

    /// <summary>
    /// 夜の画像を名前から探す。「名前」の後ろに _N・N・_night・night・_dark・dark・_夜 などを付けたもの。
    /// 連番の画像（ID_motor_0）なら、数の前に付けたもの（ID_motor_N_0・ID_motorN_0 など）も探す。見つからなければ null。
    /// </summary>
    public static string? FindNight(string day)
    {
        var dir = Path.GetDirectoryName(day) ?? "";
        var name = Path.GetFileNameWithoutExtension(day);
        var ext = Path.GetExtension(day);
        foreach (var m in NightMarks)
        {
            var f = Path.Combine(dir, name + m + ext);
            if (File.Exists(f)) return f;
        }
        var n = NumberRegex().Match(name);
        if (n.Success && n.Groups["pre"].Value.Length > 0)
        {
            var pre = n.Groups["pre"].Value;
            var core = pre.TrimEnd('_', '-', ' ');
            var sep = pre[core.Length..];
            foreach (var m in NightMarks)
                foreach (var cand in new[] { core + m + sep, pre + m.TrimStart('_', '-') + sep })
                {
                    if (cand == pre) continue;
                    var f = Path.Combine(dir, cand + n.Groups["num"].Value + n.Groups["post"].Value + ext);
                    if (File.Exists(f)) return f;
                }
        }
        return null;
    }

    /// <summary>
    /// 連番の画像: 同じフォルダで、数の前後の名前と拡張子が同じもの（ID_motor_0.png → ID_motor_1.png・ID_motor_2.png…）を、数の順に返す（自分も含む）。
    /// 数が 0 埋め（01・001）なら同じ桁数のものだけ。連番でない名前なら自分だけ。夜の画像らしい名前のものは除く。
    /// </summary>
    public static List<string> FindSequence(string file)
    {
        var dir = Path.GetDirectoryName(file) ?? "";
        var name = Path.GetFileNameWithoutExtension(file);
        var ext = Path.GetExtension(file);
        var m = NumberRegex().Match(name);
        if (!m.Success || !Directory.Exists(dir)) return [file];
        string pre = m.Groups["pre"].Value, post = m.Groups["post"].Value, num = m.Groups["num"].Value;
        bool padded = num.Length > 1 && num[0] == '0';
        var found = new List<(long N, string Path)>();
        foreach (var f in Directory.EnumerateFiles(dir, "*" + ext))
        {
            if (!Path.GetExtension(f).Equals(ext, StringComparison.OrdinalIgnoreCase)) continue;
            var fm = NumberRegex().Match(Path.GetFileNameWithoutExtension(f));
            if (!fm.Success || fm.Groups["pre"].Value != pre || fm.Groups["post"].Value != post) continue;
            var d = fm.Groups["num"].Value;
            if (padded ? d.Length != num.Length : d.Length > 1 && d[0] == '0') continue;
            if (d.Length > 9 || !long.TryParse(d, out var v)) continue;
            found.Add((v, f));
        }
        if (found.Count == 0) return [file];
        return found.OrderBy(x => x.N).Select(x => x.Path).ToList();
    }
}
