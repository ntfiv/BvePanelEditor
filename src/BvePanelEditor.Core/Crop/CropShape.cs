using System.Globalization;
using BvePanelEditor.Core.Psd;

namespace BvePanelEditor.Core.Crop;

/// <summary>
/// 切り抜く形（丸・楕円・多角形）。座標は切り抜いた画像の左上から（オブジェクトを動かすと形も一緒に動く）。
/// 1 つのオブジェクトに複数あれば足し合わせ、形の外は透明にする。
/// ファイルでは見出しのすぐ下の「;@切り抜き形: 丸 32,32 r=28」「;@切り抜き形: 楕円 32,32 rx=28 ry=20」「;@切り抜き形: 多角形 0,10 40,10 40,60」。
/// </summary>
public sealed class CropShape
{
    /// <summary>丸か楕円（中心と半径で決まる形）。楕円のときは IsEllipse も true。</summary>
    public bool IsCircle { get; init; }
    /// <summary>楕円（横の半径 R・縦の半径 Ry）。</summary>
    public bool IsEllipse { get; init; }
    public double Cx { get; set; }
    public double Cy { get; set; }
    /// <summary>半径（楕円なら横の半径）。</summary>
    public double R { get; set; }
    /// <summary>縦の半径（丸なら R と同じ）。</summary>
    public double Ry { get => IsEllipse ? _ry : R; set => _ry = value; }
    private double _ry;
    public List<(double X, double Y)> Points { get; init; } = [];

    public static CropShape Circle(double cx, double cy, double r) => new() { IsCircle = true, Cx = cx, Cy = cy, R = r };
    public static CropShape Ellipse(double cx, double cy, double rx, double ry) => new() { IsCircle = true, IsEllipse = true, Cx = cx, Cy = cy, R = rx, Ry = ry };
    public static CropShape Polygon(IEnumerable<(double X, double Y)> pts) => new() { Points = [.. pts] };

    public CropShape Clone() => new() { IsCircle = IsCircle, IsEllipse = IsEllipse, Cx = Cx, Cy = Cy, R = R, Ry = Ry, Points = [.. Points] };

    /// <summary>形を dx, dy だけずらしたもの。</summary>
    public CropShape Offset(double dx, double dy) => IsEllipse ? Ellipse(Cx + dx, Cy + dy, R, Ry)
        : IsCircle ? Circle(Cx + dx, Cy + dy, R)
        : Polygon(Points.Select(p => (p.X + dx, p.Y + dy)));

    public bool IsValid => IsCircle ? R > 0 && Ry > 0 : Points.Count >= 3;

    /// <summary>形の外枠（小数は外側へ丸める）。</summary>
    public PsdRect Bounds()
    {
        if (IsCircle)
            return new PsdRect((int)Math.Floor(Cx - R), (int)Math.Floor(Cy - Ry), (int)Math.Ceiling(Cx + R), (int)Math.Ceiling(Cy + Ry));
        if (Points.Count == 0) return default;
        return new PsdRect((int)Math.Floor(Points.Min(p => p.X)), (int)Math.Floor(Points.Min(p => p.Y)),
            (int)Math.Ceiling(Points.Max(p => p.X)), (int)Math.Ceiling(Points.Max(p => p.Y)));
    }

    public static PsdRect Bounds(IEnumerable<CropShape> shapes) =>
        shapes.Where(s => s.IsValid).Select(s => s.Bounds()).Aggregate(default(PsdRect), (u, r) => u.IsEmpty ? r : u.Union(r));

    /// <summary>点 (x, y) が形の中か。</summary>
    public bool Contains(double x, double y)
    {
        if (IsCircle) { double u = (x - Cx) / R, v = (y - Cy) / Ry; return u * u + v * v <= 1; }
        bool inside = false;
        for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++)
        {
            var (xi, yi) = Points[i];
            var (xj, yj) = Points[j];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// 形の外を透明にした画像を返す（img の左上を形の座標の 0, 0 とする）。縁は 4×4 の点で数えてなめらかにする。
    /// 形がなければそのまま返す。
    /// </summary>
    public static PsdPixels Mask(PsdPixels img, IReadOnlyList<CropShape> shapes)
    {
        var valid = shapes.Where(s => s.IsValid).ToList();
        if (valid.Count == 0) return img;
        int w = img.Width, h = img.Height;
        var bgra = (byte[])img.Bgra.Clone();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int inside = 0;
                for (int sy = 0; sy < 4; sy++)
                    for (int sx = 0; sx < 4; sx++)
                    {
                        double px = x + (sx + 0.5) / 4, py = y + (sy + 0.5) / 4;
                        if (valid.Any(s => s.Contains(px, py))) inside++;
                    }
                if (inside == 16) continue;
                int a = (y * w + x) * 4 + 3;
                bgra[a] = (byte)Math.Round(bgra[a] * inside / 16.0);
            }
        return new PsdPixels(img.Bounds, bgra);
    }

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>コメントの行の「;@切り抜き形: 」より後ろ。</summary>
    public string ToText() => IsEllipse ? $"楕円 {F(Cx)},{F(Cy)} rx={F(R)} ry={F(Ry)}"
        : IsCircle ? $"丸 {F(Cx)},{F(Cy)} r={F(R)}"
        : "多角形 " + string.Join(" ", Points.Select(p => $"{F(p.X)},{F(p.Y)}"));

    public static bool TryParse(string text, out CropShape shape)
    {
        shape = new CropShape();
        var parts = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        static bool Num(string s, out double v) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        static bool Pt(string s, out double x, out double y)
        {
            x = y = 0;
            var xy = s.Split(',');
            return xy.Length == 2 && Num(xy[0], out x) && Num(xy[1], out y);
        }
        if (parts[0] == "丸")
        {
            if (parts.Length != 3 || !Pt(parts[1], out var cx, out var cy)) return false;
            var r = parts[2];
            if (r.StartsWith("r=", StringComparison.OrdinalIgnoreCase)) r = r[2..];
            if (!Num(r, out var rv) || rv <= 0) return false;
            shape = Circle(cx, cy, rv);
            return true;
        }
        if (parts[0] == "楕円")
        {
            if (parts.Length != 4 || !Pt(parts[1], out var cx, out var cy)
                || !parts[2].StartsWith("rx=", StringComparison.OrdinalIgnoreCase) || !Num(parts[2][3..], out var rx)
                || !parts[3].StartsWith("ry=", StringComparison.OrdinalIgnoreCase) || !Num(parts[3][3..], out var ry)
                || rx <= 0 || ry <= 0) return false;
            shape = Ellipse(cx, cy, rx, ry);
            return true;
        }
        if (parts[0] == "多角形")
        {
            var pts = new List<(double, double)>();
            foreach (var p in parts.Skip(1))
            {
                if (!Pt(p, out var x, out var y)) return false;
                pts.Add((x, y));
            }
            if (pts.Count < 3) return false;
            shape = Polygon(pts);
            return true;
        }
        return false;
    }
}
