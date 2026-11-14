namespace BvePanelEditor.Core.Psd;

/// <summary>
/// グラデーション塗り（GdFl）。色の点は効果と同じ <see cref="PsdGradient"/> を使い、
/// こちらは塗りつぶし固有の「広がり方」（角度・種類・大きさ・中心のずらし）を持つ。
/// 広がり方の計算は、グラデーションオーバーレイ効果の描画（PsdEffects）と同じ約束にそろえた近似。
/// </summary>
public sealed class PsdGradientFill
{
    public PsdGradient Gradient = null!;
    public double AngleDeg;
    public string Type = "Lnr "; // Lnr （線形）・Rdl （円形）・Angl（角度）・Rflc（反射）・Dmnd（菱形）
    public double Scale = 100;   // %
    public bool Reverse;
    public bool AlignWithLayer;
    public double OffsetX, OffsetY; // 中心のずらし（px）

    public static PsdGradientFill? Parse(byte[] data)
    {
        if (PsdDescriptor.ReadVersioned(data) is not { } d) return null;
        if (!d.TryGetValue("Grad", out var go) || go is not Dictionary<string, object> g) return null;
        if (PsdEffects.ReadGradient(g) is not { } grad) return null; // 色の点が無い・ノイズグラデーションは使えない
        var fill = new PsdGradientFill { Gradient = grad };
        fill.AngleDeg = PsdDescriptor.Number(d, "Angl") ?? 0;
        if (d.TryGetValue("Type", out var ty) && ty is string ts) fill.Type = ts;
        fill.Scale = PsdDescriptor.Number(d, "Scl ") is { } sc && sc > 0 ? sc : 100;
        fill.Reverse = d.TryGetValue("Rvrs", out var rv) && rv is true;
        fill.AlignWithLayer = d.TryGetValue("Algn", out var al) && al is true;
        if (d.TryGetValue("Ofst", out var oo) && oo is Dictionary<string, object> ofst)
        {
            fill.OffsetX = PsdDescriptor.Number(ofst, "Hrzn") ?? 0;
            fill.OffsetY = PsdDescriptor.Number(ofst, "Vrtc") ?? 0;
        }
        return fill;
    }

    /// <summary>キャンバス座標（x, y）に対応するグラデーションの位置（0〜1）。area は広がりの基準の矩形。</summary>
    public double Position(double x, double y, PsdRect area)
    {
        double cx = (area.Left + area.Right) / 2.0 + OffsetX, cy = (area.Top + area.Bottom) / 2.0 + OffsetY;
        double px = x + 0.5 - cx, py = y + 0.5 - cy;
        double rad = AngleDeg * Math.PI / 180.0;
        double ux = Math.Cos(rad), uy = -Math.Sin(rad);
        double scale = Scale / 100.0;
        double len = Math.Max(1, (Math.Abs(area.Width * ux) + Math.Abs(area.Height * uy)) * scale);
        double radius = Math.Max(1, Math.Sqrt((double)area.Width * area.Width + (double)area.Height * area.Height) / 2 * scale);
        double along = px * ux + py * uy, across = -px * uy + py * ux;
        double t = Type switch
        {
            "Rdl " => Math.Sqrt(px * px + py * py) / radius,
            "Rflc" => Math.Abs(along) / (len / 2),
            "Dmnd" => (Math.Abs(along) + Math.Abs(across)) / radius,
            "Angl" => ((Math.Atan2(-py, px) - rad) / (2 * Math.PI) % 1 + 1) % 1,
            _ => along / len + 0.5, // "Lnr " 線形
        };
        if (Reverse) t = 1 - t;
        return Math.Clamp(t, 0, 1);
    }
}
