using System.Globalization;

namespace BvePanelEditor.Core;

/// <summary>
/// 4 隅で変形して貼りこんだオブジェクトの、変形の方法（元の画像・4 隅・1 コマの高さ）。
/// 元の画像を変えたときも、同じ 4 隅で変形し直せるように覚えておく。
/// ファイルでは見出しのすぐ下の「;@変形: 元=src\monitor.png | 夜=src\monitor_N.png | 隅=210,110 320,125 318,215 205,222 | コマ=10 | 倍率=2」。
/// </summary>
public sealed class WarpInfo
{
    /// <summary>元の画像（パネルファイルからの場所。別のドライブならフルパス）。SVG のこともある。</summary>
    public string Source { get; set; } = "";
    /// <summary>夜の元の画像（なければ null）。</summary>
    public string? Night { get; set; }
    /// <summary>4 隅（左上・右上・右下・左下、パネルの座標）。</summary>
    public (double X, double Y)[] Corners { get; set; } = new (double, double)[4];
    /// <summary>DigitalNumber の元の画像の 1 コマの高さ（常点灯なら 0）。</summary>
    public int Interval { get; set; }
    /// <summary>元の画像が SVG のとき、PNG にした倍率（SVG でなければ null）。</summary>
    public double? SvgScale { get; set; }

    public WarpInfo Clone() => new() { Source = Source, Night = Night, Corners = (ValueTuple<double, double>[])Corners.Clone(), Interval = Interval, SvgScale = SvgScale };

    public string ToText()
    {
        var parts = new List<string> { "元=" + Source };
        if (Night != null) parts.Add("夜=" + Night);
        parts.Add("隅=" + string.Join(" ", Corners.Select(c => $"{PanelValues.Num(c.X)},{PanelValues.Num(c.Y)}")));
        if (Interval > 0) parts.Add("コマ=" + Interval.ToString(CultureInfo.InvariantCulture));
        if (SvgScale is { } k) parts.Add("倍率=" + PanelValues.Num(k));
        return string.Join(" | ", parts);
    }

    /// <summary>「元=… | 隅=…」の形を読む。元か 4 隅が読めなければ false。</summary>
    public static bool TryParse(string text, out WarpInfo info)
    {
        info = new WarpInfo();
        bool corners = false;
        foreach (var raw in text.Split('|'))
        {
            var part = raw.Trim();
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq].Trim();
            var v = part[(eq + 1)..].Trim();
            switch (key)
            {
                case "元": info.Source = v; break;
                case "夜": info.Night = v.Length > 0 ? v : null; break;
                case "コマ": if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv) && iv > 0) info.Interval = iv; break;
                case "倍率": if (PanelValues.TryDouble(v, out var k) && k > 0) info.SvgScale = k; break;
                case "隅":
                    {
                        var pts = v.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (pts.Length != 4) break;
                        var c = new (double, double)[4];
                        bool ok = true;
                        for (int i = 0; i < 4 && ok; i++) ok = PanelValues.TryPoint(pts[i], out c[i].Item1, out c[i].Item2);
                        if (ok) { info.Corners = c; corners = true; }
                        break;
                    }
            }
        }
        return info.Source.Length > 0 && corners;
    }
}

public sealed partial class PanelDocument
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*[;#]+\s*@変形\s*[:：]\s*(?<v>.*?)\s*$")]
    private static partial System.Text.RegularExpressions.Regex WarpRegex();

    /// <summary>「;@変形: 元=… | 隅=…」の行か。中身が読めなければ false（ふつうのコメントとして残す）。</summary>
    public static bool TryWarpLine(string raw, out WarpInfo info)
    {
        info = new WarpInfo();
        var m = WarpRegex().Match(raw);
        return m.Success && WarpInfo.TryParse(m.Groups["v"].Value, out info);
    }

    public static string WarpLine(WarpInfo w) => ";@変形: " + w.ToText();
}
