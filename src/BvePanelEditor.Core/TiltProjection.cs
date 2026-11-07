namespace BvePanelEditor.Core;

/// <summary>
/// 書式 2.01 の Tilt（計器の面の傾き）を、プレビュー用に画面へ移す。
/// BVE の資料にあるのは「angleX: X 軸まわり・上向きが正」「angleY: Y 軸まわり・左向きが正」「Perspective: 大きいほど遠近が弱い（既定 1）」だけなので、
/// BVE6 の画面（E217 の調整用の目盛り画像を Tilt 47 で置いたもの）と比べて、次の式を決めた（ずれは平均 0.6 画素ほど）。
/// 1. 面上の点（計器の Location からのずれ u, v。下が +v）を X 軸まわりに回す（上側が奥へ）、続けて Y 軸まわりに回す（右側が奥へ）
/// 2. 奥行き z の点を、[This] の Origin（消失点）から見て d / (d + z) 倍の所に置く。d = Resolution × Perspective
/// 面上の点から画面の点への写し方は射影変換（3×3 の行列）になるので、逆向き（画面 → 面）も求められる。
/// </summary>
public sealed class TiltProjection
{
    /// <summary>カメラまでの距離の係数（Resolution × Perspective に掛ける）。BVE6 の画面と比べて 1。</summary>
    public const double DistanceFactor = 1.0;
    /// <summary>Resolution がないときの代わり。</summary>
    public const double DefaultResolution = 1024;

    public double CenterX { get; }
    public double CenterY { get; }
    public double AngleX { get; }
    public double AngleY { get; }
    public double Distance { get; }

    // 面 (u, v, 1) → 同次座標 (画面のずれ·w, w)。行列 H（3×3）
    private readonly double[] _h = new double[9];
    // 逆行列（画面のずれ → 面）
    private readonly double[] _inv = new double[9];

    private TiltProjection(double cx, double cy, double ax, double ay, double distance, double camX, double camY)
    {
        CenterX = cx; CenterY = cy; AngleX = ax; AngleY = ay; Distance = distance;
        double rx = ax * Math.PI / 180, ry = ay * Math.PI / 180;
        double sx = Math.Sin(rx), cosX = Math.Cos(rx), sy = Math.Sin(ry), cosY = Math.Cos(ry);
        // X 軸まわり: (u, v·cosX, −v·sinX)、続けて Y 軸まわり（右側が奥）
        // → X = a·u + b·v、Y = c·v、Z = e·u + f·v
        double a = cosY, b = sx * sy, c = cosX, e = sy, f = -sx * cosY, d = distance;
        // カメラ（消失点）から見た遠近: 画面 = cam + (L + (X, Y) − cam)·d / (d + Z)
        // L からのずれ = ((X, Y)·d + (cam − L)·Z) / (d + Z)
        double ox = camX - cx, oy = camY - cy;
        double[] h = [a * d + ox * e, b * d + ox * f, 0, oy * e, c * d + oy * f, 0, e, f, d];
        Array.Copy(h, _h, 9);
        Invert(h, _inv);
    }

    /// <summary>
    /// Tilt の角度があれば作る（0, 0 なら null。描くときは今までどおり）。
    /// cx, cy は傾ける中心（計器の Location）。resolution・perspective は [This] の値（0 以下なら既定）。
    /// camX, camY は遠近の中心（[This] の Origin）。
    /// </summary>
    public static TiltProjection? Create(double cx, double cy, double angleX, double angleY, double resolution, double perspective, double camX, double camY)
    {
        if (Math.Abs(angleX) < 1e-9 && Math.Abs(angleY) < 1e-9) return null;
        // 真横（90°）は面が線になる。少しだけ手前で止める
        angleX = Math.Clamp(angleX, -89.5, 89.5);
        angleY = Math.Clamp(angleY, -89.5, 89.5);
        double res = resolution > 0 ? resolution : DefaultResolution;
        double per = perspective > 0 ? perspective : 1;
        return new TiltProjection(cx, cy, angleX, angleY, res * per * DistanceFactor, camX, camY);
    }

    /// <summary>遠近の中心を計器の位置にする（[This] の Origin が分からないとき）。</summary>
    public static TiltProjection? Create(double cx, double cy, double angleX, double angleY, double resolution, double perspective) =>
        Create(cx, cy, angleX, angleY, resolution, perspective, cx, cy);

    /// <summary>平らなときのパネル座標の点（x, y）を、傾けた画面の点へ。</summary>
    public (double X, double Y) Project(double x, double y)
    {
        double u = x - CenterX, v = y - CenterY;
        double nx = _h[0] * u + _h[1] * v, ny = _h[3] * u + _h[4] * v, w = _h[6] * u + _h[7] * v + _h[8];
        // 奥行きがカメラより手前に来るほど傾いたときは、ほぼ平行に移す
        if (w < Distance * 0.05) w = Distance * 0.05;
        return (CenterX + nx / w, CenterY + ny / w);
    }

    /// <summary>画面の点を、平らなときのパネル座標の点へ戻す（当たり判定用）。</summary>
    public (double X, double Y) Unproject(double x, double y)
    {
        double px = x - CenterX, py = y - CenterY;
        double u = _inv[0] * px + _inv[1] * py + _inv[2];
        double v = _inv[3] * px + _inv[4] * py + _inv[5];
        double w = _inv[6] * px + _inv[7] * py + _inv[8];
        if (Math.Abs(w) < 1e-12) return (double.NaN, double.NaN);
        return (CenterX + u / w, CenterY + v / w);
    }

    private static void Invert(double[] m, double[] r)
    {
        double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
        double A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
        double det = a * A + b * B + c * C;
        if (Math.Abs(det) < 1e-18) { Array.Clear(r); r[0] = r[4] = r[8] = 1; return; }
        double s = 1 / det;
        r[0] = A * s; r[1] = -(b * i - c * h) * s; r[2] = (b * f - c * e) * s;
        r[3] = B * s; r[4] = (a * i - c * g) * s; r[5] = -(a * f - c * d) * s;
        r[6] = C * s; r[7] = -(a * h - b * g) * s; r[8] = (a * e - b * d) * s;
    }
}
