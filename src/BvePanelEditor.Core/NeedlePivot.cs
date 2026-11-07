using BvePanelEditor.Core.Psd;

namespace BvePanelEditor.Core;

/// <summary>針の画像から見つけた回転の中心（Needle の Origin）。Found が false なら推定（軸の丸が見つからなかった）。</summary>
public sealed record NeedlePivotResult(int X, int Y, string Reason, bool Found);

/// <summary>
/// 針の画像（上向きに描いたもの）から、回転の中心を見つける。
/// 1. 軸の丸: 行ごとの幅が「円」の形にふくらんでいる所のうち、いちばん大きいもの。その中心
/// 2. 丸がなければ、針の下の端から幅の半分だけ上（推定）
/// 横の位置は、針全体の左右の中心。
/// </summary>
public static class NeedlePivot
{
    public static NeedlePivotResult? Find(PsdPixels img)
    {
        int w = img.Width, h = img.Height;
        if (w == 0 || h == 0) return null;
        var width = new double[h];
        double sumCenter = 0;
        int rows = 0, top = -1, bottom = -1;
        for (int y = 0; y < h; y++)
        {
            int l = -1, r = -1;
            for (int x = 0; x < w; x++)
                if (img.Bgra[(y * w + x) * 4 + 3] > 64) { if (l < 0) l = x; r = x; }
            if (l < 0) continue;
            width[y] = r - l + 1;
            sumCenter += (l + r) / 2.0;
            rows++;
            if (top < 0) top = y;
            bottom = y;
        }
        if (top < 0) return null;
        int cx = (int)Math.Round(sumCenter / rows);

        // 円らしさ: 中心 y・半径 r（幅の半分）の円の幅と、実際の幅の差の平均（幅に対する割合）
        (double Err, int Y, double W)? best = null;
        for (int y = top; y <= bottom; y++)
        {
            double pw = width[y];
            if (pw < 4 || (y > top && pw < width[y - 1]) || (y < bottom && pw < width[y + 1])) continue;
            double r = pw / 2, err = 0;
            int n = 0;
            for (int dy = -(int)r; dy <= (int)r; dy++)
            {
                int yy = y + dy;
                double expect = 2 * Math.Sqrt(Math.Max(0, r * r - dy * dy));
                err += (yy < 0 || yy >= h ? pw : Math.Abs(width[yy] - expect)) / pw;
                n++;
            }
            err /= n;
            if (err >= 0.12) continue;
            if (best == null || pw > best.Value.W || (pw == best.Value.W && err < best.Value.Err)) best = (err, y, pw);
        }
        if (best is { } b) return new NeedlePivotResult(cx, b.Y, $"軸の丸（直径 {b.W:0}px）の中心", true);
        return new NeedlePivotResult(cx, (int)Math.Round(bottom - width[bottom] / 2),
            "軸の丸が見つからないので、針の下の端から幅の半分だけ上にしました（違っていたら直してください）", false);
    }
}
