using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BvePanelEditor;

/// <summary>
/// 確認用: 表示範囲・画面枠の出し入れ（--frametest 画面.png 結果.txt）。
/// [This] を選んでいても、チェックを外せば枠を描かないこと、表示範囲の外を暗くすることを確かめる。
/// </summary>
public partial class MainWindow
{
    public async void RunFrameTest(string png, string report)
    {
        var log = new List<string>();
        int fail = 0;
        void Check(string name, bool ok) { log.Add($"{(ok ? "OK  " : "FAIL")} {name}"); if (!ok) fail++; }
        try
        {
            Check("メニューの名前は「パネルファイルの内容を表示」", ShowTextMenu.Header as string == "パネルファイルの内容を表示");
            SelectForStartup(_doc!.Sections.IndexOf(_doc.This!));
            Canvas.FitAll();
            UpdateLayout();
            await Task.Delay(500);
            var on = Grab();
            SaveWindowPng(this, png);

            ShowFrameMenu.IsChecked = false;
            OnToggleFrame(ShowFrameMenu, new RoutedEventArgs());
            UpdateLayout();
            await Task.Delay(300);
            var off = Grab();
            SaveWindowPng(this, Path.ChangeExtension(png, ".off.png"));

            // 表示範囲の外の点（左上の角の近く）: 枠ありは 30% ほど暗い
            var lim = _scene!.ViewLimit;
            var outside = Canvas.ToScreen(new Point(lim.Left - 20, lim.Top - 20));
            var inside = Canvas.ToScreen(new Point(lim.Left + lim.Width / 2, lim.Top + lim.Height / 2));
            double ratioOut = Bright(on, outside) / Math.Max(1, Bright(off, outside));
            double ratioIn = Bright(on, inside) / Math.Max(1, Bright(off, inside));
            log.Add($"     明るさの比（枠あり／なし）: 表示範囲の外 {ratioOut:0.00}・中 {ratioIn:0.00}");
            Check("表示範囲の外は 30% ほど暗くする（0.6〜0.8）", ratioOut is > 0.6 and < 0.8);
            Check("表示範囲の中は暗くしない", Math.Abs(ratioIn - 1) < 0.05);
            int diff = 0;
            for (int i = 0; i < on.Px.Length; i += 4) if (on.Px[i] != off.Px[i] || on.Px[i + 1] != off.Px[i + 1]) diff++;
            Check("[This] を選んでいても、チェックを外すと枠が消える（画面が変わる）", diff > 1000);

            ShowFrameMenu.IsChecked = true;
            OnToggleFrame(ShowFrameMenu, new RoutedEventArgs());
        }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); fail++; }
        log.Add(fail == 0 ? "ALL PASSED" : $"{fail} FAILED");
        File.WriteAllLines(report, log);
        _savedText = Editor.Text;
        Close();
    }

    private (byte[] Px, int W, int H) Grab()
    {
        int w = (int)Canvas.ActualWidth, h = (int)Canvas.ActualHeight;
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(Canvas);
        var px = new byte[w * h * 4];
        rtb.CopyPixels(px, w * 4, 0);
        return (px, w, h);
    }

    private static double Bright((byte[] Px, int W, int H) img, Point p)
    {
        int x = (int)p.X, y = (int)p.Y;
        if (x < 0 || y < 0 || x >= img.W || y >= img.H) return double.NaN;
        int i = (y * img.W + x) * 4;
        return (img.Px[i] + img.Px[i + 1] + img.Px[i + 2]) / 3.0;
    }
}
