using System.Text.RegularExpressions;

namespace BvePanelEditor.Core;

public sealed record SubjectInfo(string Key, string Label, double Min, double Max, bool IsBool = false);

/// <summary>Subject（状態量）の一覧と、状態からの値の求め方。</summary>
public static partial class Subjects
{
    public static readonly SubjectInfo[] Standard =
    [
        new("kmph", "速度 km/h", 0, 160),
        new("bc", "ブレーキシリンダ圧 kPa", 0, 600),
        new("mr", "元空気溜め圧 kPa", 0, 1000),
        new("sap", "直通管圧 kPa", 0, 600),
        new("bp", "ブレーキ管圧 kPa", 0, 600),
        new("er", "釣り合い空気溜め圧 kPa", 0, 600),
        new("am", "電流 A（力行が正）", -1000, 1000),
        new("door", "全ドア閉", 0, 1, true),
        new("csc", "定速制御中", 0, 1, true),
        new("power", "力行ノッチ", 0, 5),
        new("brake", "ブレーキノッチ", 0, 9),
        new("hour", "時刻（時）", 0, 24),
        new("min", "時刻（分）", 0, 60),
        new("sec", "時刻（秒）", 0, 60),
    ];

    /// <summary>プロパティ欄の候補（サフィックス付きと ats も含む）。</summary>
    public static IEnumerable<string> Suggestions()
    {
        yield return "true";
        foreach (var s in Standard) yield return s.Key;
        yield return "kmphd0"; yield return "kmphd1"; yield return "kmphd2";
        yield return "bcd1"; yield return "bcd2";
        yield return "mrd1"; yield return "mrd2";
        yield return "amabs";
        for (int i = 0; i < 4; i++) { yield return $"doorl{i}"; yield return $"doorr{i}"; }
        for (int i = 0; i < 256; i++) yield return $"ats{i}";
    }

    [GeneratedRegex(@"^(kmph|bc|mr)d([0-2])$", RegexOptions.IgnoreCase)]
    private static partial Regex DigitRegex();

    [GeneratedRegex(@"^door[lr]\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex DoorCarRegex();

    [GeneratedRegex(@"^ats(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex AtsRegex();

    /// <summary>状態として持つキー（kmphd1 → kmph、amabs → am）。true は null。</summary>
    public static string? BaseKey(string subject)
    {
        var s = subject.Trim().ToLowerInvariant();
        if (s.Length == 0 || s == "true") return null;
        var m = DigitRegex().Match(s);
        if (m.Success) return m.Groups[1].Value;
        if (s == "amabs") return "am";
        return s;
    }

    public static bool IsAts(string key, out int index)
    {
        index = -1;
        var m = AtsRegex().Match(key);
        return m.Success && int.TryParse(m.Groups[1].Value, out index) && index is >= 0 and <= 255;
    }

    /// <summary>BVE 本体の ats（0〜255）と、BveEX の ats（256 以上）のどちらか。</summary>
    public static bool IsAnyAts(string key, out int index)
    {
        index = -1;
        var m = AtsRegex().Match(key.Trim());
        return m.Success && int.TryParse(m.Groups[1].Value, out index) && index >= 0;
    }

    /// <summary>BveEX で使える ats（256 以上。BVE 本体の ats は 0〜255）。</summary>
    public static bool IsBveExAts(string subject, out int index) => IsAnyAts(subject.Trim().ToLowerInvariant(), out index) && index > 255;

    public static bool IsKnown(string subject)
    {
        var s = subject.Trim().ToLowerInvariant();
        if (s == "true" || s == "amabs") return true;
        if (DigitRegex().IsMatch(s) || DoorCarRegex().IsMatch(s)) return true;
        if (IsAts(s, out _)) return true;
        return Standard.Any(x => x.Key == s);
    }

    public static SubjectInfo Info(string baseKey)
    {
        var std = Standard.FirstOrDefault(x => x.Key == baseKey);
        if (std != null) return std;
        if (DoorCarRegex().IsMatch(baseKey))
        {
            var side = baseKey[4] == 'l' ? "左" : "右";
            return new SubjectInfo(baseKey, $"{baseKey[5..]} 号車{side}ドア開", 0, 1, true);
        }
        if (IsAnyAts(baseKey, out var i)) return new SubjectInfo(baseKey, i > 255 ? $"ATS パネル {i}（BveEX）" : $"ATS パネル {i}", 0, 10);
        return new SubjectInfo(baseKey, "（不明な状態量）", 0, 100);
    }

    public static string Describe(string subject)
    {
        var s = subject.Trim().ToLowerInvariant();
        if (s == "true") return "常表示";
        var m = DigitRegex().Match(s);
        if (m.Success)
        {
            var place = m.Groups[2].Value switch { "0" => "1 の位", "1" => "10 の位", _ => "100 の位" };
            return $"{Info(m.Groups[1].Value).Label} の{place}";
        }
        if (s == "amabs") return "電流の絶対値 A";
        var b = BaseKey(s);
        return b == null ? "" : Info(b).Label;
    }

    /// <summary>状態（BaseKey → 値）から Subject の値を求める。</summary>
    public static double Resolve(string subject, IReadOnlyDictionary<string, double> state)
    {
        var s = subject.Trim().ToLowerInvariant();
        if (s == "true") return 1;
        var m = DigitRegex().Match(s);
        if (m.Success)
        {
            double v = state.GetValueOrDefault(m.Groups[1].Value);
            int place = m.Groups[2].Value[0] - '0';
            long n = (long)Math.Floor(Math.Abs(v));
            for (int i = 0; i < place; i++) n /= 10;
            return n % 10;
        }
        if (s == "amabs") return Math.Abs(state.GetValueOrDefault("am"));
        return state.GetValueOrDefault(s);
    }
}
