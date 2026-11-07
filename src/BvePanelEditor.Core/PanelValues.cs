using System.Globalization;

namespace BvePanelEditor.Core;

/// <summary>パネルファイルの値（数値・座標・色）の読み書き。</summary>
public static class PanelValues
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static bool TryDouble(string? s, out double v)
    {
        v = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        return double.TryParse(s.Trim(), NumberStyles.Float, Inv, out v);
    }

    public static double Double(string? s, double fallback) => TryDouble(s, out var v) ? v : fallback;

    public static bool TryPoint(string? s, out double x, out double y)
    {
        x = y = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var parts = s.Split(',');
        if (parts.Length < 2) return false;
        return TryDouble(parts[0], out x) && TryDouble(parts[1], out y);
    }

    /// <summary>"FF0000" → (255,0,0)。8 桁なら下位 6 桁を色として読む。</summary>
    public static bool TryColor(string? s, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim().TrimStart('#');
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        if (t.Length == 8) t = t[2..];
        // 3 桁（#00F）は各桁を 2 回ずつ（#0000FF）
        if (t.Length == 3) t = string.Concat(t.Select(ch => new string(ch, 2)));
        if (t.Length != 6 || !uint.TryParse(t, NumberStyles.HexNumber, Inv, out var c)) return false;
        r = (byte)(c >> 16);
        g = (byte)(c >> 8);
        b = (byte)c;
        return true;
    }

    public static bool TryBool(string? s, out bool v)
    {
        v = false;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim();
        if (t.Equals("true", StringComparison.OrdinalIgnoreCase) || t == "1") { v = true; return true; }
        if (t.Equals("false", StringComparison.OrdinalIgnoreCase) || t == "0") { v = false; return true; }
        return false;
    }

    public static string Num(double v)
    {
        if (Math.Abs(v - Math.Round(v)) < 1e-9) return ((long)Math.Round(v)).ToString(Inv);
        return v.ToString("0.###", Inv);
    }

    public static string Point(double x, double y) => $"{Num(x)}, {Num(y)}";

    public static string Color(byte r, byte g, byte b) => $"{r:X2}{g:X2}{b:X2}";
}
