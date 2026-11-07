using System.IO;
using BvePanelEditor.Core;
using BvePanelEditor.Core.Psd;

namespace BvePanelEditor;

/// <summary>
/// 確認用: Tilt（傾き）のプレビュー（--tilttest 作業フォルダ 画面.png 結果.txt）。
/// 作業フォルダに、同梱の E217 の針で「平らな針」「Tilt 47 の針」「Tilt 47 のデジタルゲージ」のパネルを作って開く。
/// </summary>
public partial class MainWindow
{
    public async void RunTiltTest(string workDir, string png, string report)
    {
        var log = new List<string>();
        int fail = 0;
        void Check(string name, bool ok) { log.Add($"{(ok ? "OK  " : "FAIL")} {name}"); if (!ok) fail++; }
        try
        {
            Directory.CreateDirectory(workDir);
            var needleSrc = Path.Combine(NeedleCatalog.E217Folder, "needle_speed_E217.png");
            File.Copy(needleSrc, Path.Combine(workDir, "needle_speed.png"), true);
            var gray = new byte[1024 * 1024 * 4];
            for (int i = 0; i < gray.Length; i += 4) { gray[i] = gray[i + 1] = gray[i + 2] = 90; gray[i + 3] = 255; }
            File.WriteAllBytes(Path.Combine(workDir, "base.png"), PngWriter.Encode(1024, 1024, gray));
            string Needle(int x, int y, string tilt) => $"""

                [Needle]
                Subject = kmph
                Location = {x}, {y}
                DaytimeImage = needle_speed.png
                Origin = 8, 256
                Layer = 3
                Radius = 180
                InitialAngle = -120
                LastAngle = 120
                Minimum = 0
                Maximum = 160
                {tilt}
                """;
            var text = "BveTs Instrument Panel 2.01\r\n\r\n[This]\r\nResolution = 1024\r\nDaytimeImage = base.png\r\nPerspective = 1\r\n"
                + Needle(300, 450, "") + Needle(700, 450, "Tilt = 47, 0")
                + "\r\n\r\n[DigitalGauge]\r\nSubject = kmph\r\nLocation = 500, 820\r\nColor = 000000\r\nLayer = 2\r\nRadius = 120\r\nInitialAngle = -120\r\nLastAngle = 120\r\nMinimum = 0\r\nMaximum = 160\r\nTilt = 47, 0\r\n";
            var panel = Path.Combine(workDir, "panel.txt");
            File.WriteAllText(panel, text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
            LoadPanel(panel, null);
            _states.SetValue("kmph", 80); // 針は真上（角度 0）
            SelectForStartup(-1);
            Canvas.FitAll();
            UpdateLayout();
            await Task.Delay(1500);

            var items = _scene!.Items;
            var flat = items.First(i => i.Kind == SectionKind.Needle && i.Location.X == 300);
            var tilted = items.First(i => i.Kind == SectionKind.Needle && i.Location.X == 700);
            var gauge = items.First(i => i.Kind == SectionKind.DigitalGauge);
            Check("平らな針には傾きがなく、Tilt の針・ゲージにはある", flat.Tilt == null && tilted.Tilt != null && gauge.Tilt != null);
            static double Height(System.Windows.Point[] o) => o.Max(p => p.Y) - o.Min(p => p.Y);
            static double Width(System.Windows.Point[] o) => o.Max(p => p.X) - o.Min(p => p.X);
            double ratio = Height(tilted.Outline()) / Height(flat.Outline());
            log.Add($"     真上を指す針の高さ: 平ら {Height(flat.Outline()):0.0} / Tilt 47 {Height(tilted.Outline()):0.0}（{ratio:0.000} 倍。cos47 = {Math.Cos(47 * Math.PI / 180):0.000}）");
            Check("Tilt 47 の針は縦が縮む（0.6〜0.8 倍）", ratio is > 0.6 and < 0.8);
            var go = gauge.Outline();
            log.Add($"     ゲージの枠: 幅 {Width(go):0.0} / 高さ {Height(go):0.0}");
            Check("Tilt 47 のゲージは横長になる", Height(go) < Width(go) * 0.8);

            // 当たり判定: 傾けた針の上の点で選べ、平らなときの位置では選ばない
            var mid = tilted.Project(new System.Windows.Point(700, 450 - 120));
            log.Add($"     針の 2/3 の所: 平ら (700, 330) → 傾けると ({mid.X:0.0}, {mid.Y:0.0})");
            Check("傾けた針の上をクリックすると、その針を選ぶ", Canvas.HitForTest(mid) == tilted.Section);
            Check("平らなときの位置（傾けると外れる所）では、その針を選ばない", Canvas.HitForTest(new System.Windows.Point(700, 450 - 165)) != tilted.Section);

            // 描いた絵: 傾けた針は、平らな針より短く描かれる（真上を指すので、色の付いた所の上端を比べる）
            var g = Grab();
            int Top(double panelX)
            {
                var s = Canvas.ToScreen(new System.Windows.Point(panelX, 450));
                int sx = (int)Math.Round(s.X), sy = (int)Math.Round(s.Y);
                int top = sy;
                for (int y = sy; y >= 0; y--)
                {
                    bool hit = false;
                    for (int dx = -2; dx <= 2 && !hit; dx++)
                    {
                        int k = (y * g.W + sx + dx) * 4;
                        if (k < 0 || k + 2 >= g.Px.Length) continue;
                        int d = Math.Abs(g.Px[k] - 90) + Math.Abs(g.Px[k + 1] - 90) + Math.Abs(g.Px[k + 2] - 90);
                        hit = d > 60;
                    }
                    if (hit) top = y;
                    else if (sy - y > 20 && top - y > 12) break;
                }
                return sy - top;
            }
            int flatLen = Top(300), tiltLen = Top(700);
            log.Add($"     画面上の針の長さ（中心から上）: 平ら {flatLen}px / Tilt 47 {tiltLen}px");
            Check("画面でも、傾けた針は短く描かれる", flatLen > 0 && tiltLen > 0 && tiltLen < flatLen * 0.85 && tiltLen > flatLen * 0.55);

            // 書式 2.00 では Tilt は効かない
            var v200 = text.Replace("Panel 2.01", "Panel 2.00");
            File.WriteAllText(panel, v200);
            LoadPanel(panel, null);
            Check("書式 2.00 でも Tilt を描く（BVE 5.8 以降は読む）", _scene!.Items.Count(i => i.Tilt != null) == 2 && !_scene.Problems.Any(p => p.Message.Contains("Tilt")));
            File.WriteAllText(panel, text);
            LoadPanel(panel, null);
            _states.SetValue("kmph", 100);
            SelectSection(_scene!.Items.First(i => i.Kind == SectionKind.Needle && i.Location.X == 700).Section);
            Canvas.FitAll();
            UpdateLayout();
            await Task.Delay(1200);
            SaveWindowPng(this, png);
        }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); fail++; }
        log.Add(fail == 0 ? "ALL PASSED" : $"{fail} FAILED");
        File.WriteAllLines(report, log);
        _savedText = Editor.Text;
        Close();
    }

    /// <summary>確認用: 開いたパネルを x, y を中心に zoom 倍で描き、パネル画面だけを保存する（--tiltshot x,y,zoom 画面.png）。
    /// 画面.txt に「パネル座標 → 画面」の対応（左上のずれと倍率）を書く。BVE の画面と比べる用。</summary>
    public async void RunTiltShot(string view, string png)
    {
        var v = view.Split(',');
        await Task.Delay(1500);
        SelectForStartup(-1);
        Canvas.ZoomCenter(double.Parse(v[2], System.Globalization.CultureInfo.InvariantCulture));
        Canvas.CenterOn(new System.Windows.Point(double.Parse(v[0], System.Globalization.CultureInfo.InvariantCulture), double.Parse(v[1], System.Globalization.CultureInfo.InvariantCulture)));
        UpdateLayout();
        await Task.Delay(2500);
        var g = Grab();
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(g.W, g.H, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null, g.Px, g.W * 4);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using (var fs = File.Create(png)) enc.Save(fs);
        var o = Canvas.ToScreen(new System.Windows.Point(0, 0));
        var one = Canvas.ToScreen(new System.Windows.Point(1000, 0));
        File.WriteAllText(Path.ChangeExtension(png, ".txt"), FormattableString.Invariant($"{o.X} {o.Y} {(one.X - o.X) / 1000}"));
        _savedText = Editor.Text;
        Close();
    }
}
