using System.IO;
using System.Windows;
using BvePanelEditor.Core;
using BvePanelEditor.Core.Psd;
using BvePanelEditor.Rendering;
using BvePanelEditor.Views;

namespace BvePanelEditor;

/// <summary>
/// 確認用: モニタくんに合わせた読み込み（SVG・4 隅の変形・パネルファイルの一部の取り込み）
/// （--monitorpartstest 作業フォルダ 画面.png 結果.txt）。作業フォルダに確認用のパネルと画像を作って使う。
/// </summary>
public partial class MainWindow
{
    public async void RunMonitorPartsTest(string workDir, string png, string report)
    {
        var log = new List<string>();
        int fail = 0;
        void Check(string name, bool ok) { log.Add($"{(ok ? "OK  " : "FAIL")} {name}"); if (!ok) fail++; }
        var stem = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(png))!, Path.GetFileNameWithoutExtension(png));
        try
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
            Directory.CreateDirectory(workDir);
            // パネル（512×384 の灰色に、斜めの面の目安の濃い四角）
            int pw = 512, ph = 384;
            var gray = new byte[pw * ph * 4];
            for (int y = 0; y < ph; y++)
                for (int x = 0; x < pw; x++)
                {
                    int o = (y * pw + x) * 4;
                    byte v = (byte)(x >= 200 && x < 330 && y >= 100 && y < 230 ? 40 : 110);
                    gray[o] = gray[o + 1] = gray[o + 2] = v; gray[o + 3] = 255;
                }
            File.WriteAllBytes(Path.Combine(workDir, "base.png"), PngWriter.Encode(pw, ph, gray));
            var panel = Path.Combine(workDir, "panel.txt");
            File.WriteAllText(panel, "BveTs Instrument Panel 2.00\r\n\r\n[This]\r\nResolution = 512\r\nDaytimeImage = base.png\r\n");
            LoadPanel(panel, null);
            await Task.Delay(300);

            // ---- 1. SVG を常点灯画像として（倍率 2） ----
            var svg = Path.Combine(workDir, "src", "sign.svg");
            Directory.CreateDirectory(Path.GetDirectoryName(svg)!);
            File.WriteAllText(svg, """<svg xmlns="http://www.w3.org/2000/svg" width="40" height="20"><rect width="40" height="20" fill="#FF0000"/><circle cx="30" cy="10" r="6" fill="#0000FF"/></svg>""");
            ChoiceDialog.TestAnswer = (_, _) => 0;
            string? askedSvg = null;
            SvgScaleAnswer = m => { askedSvg = m; return "2"; };
            int before = _doc!.Sections.Count;
            ImportImagesToPanel([svg], new Point(100, 60));
            await Task.Delay(200);
            Check($"SVG の大きさを聞く（{askedSvg?.Split('\n').LastOrDefault()}）", askedSvg != null && askedSvg.Contains("40×20"));
            var svgSec = _doc.Sections.Count == before + 1 ? _doc.Sections[^1] : null;
            var svgRel = svgSec?.Get("DaytimeImage") ?? "";
            Check($"SVG を PNG にして常点灯画像として置く（{svgRel}）", svgSec?.Kind == SectionKind.PilotLamp && svgSec.Get("Subject") == "true" && svgRel.StartsWith("svg"));
            var svgPx = ImageLoader.TryLoadPixels(Path.Combine(workDir, svgRel));
            Check($"倍率 2 の大きさ（{svgPx?.Width}×{svgPx?.Height}）", svgPx is { Width: 80, Height: 40 });
            bool red = svgPx != null && svgPx.Bgra[(10 * 80 + 10) * 4 + 2] > 200 && svgPx.Bgra[(10 * 80 + 10) * 4] < 50;
            bool blue = svgPx != null && svgPx.Bgra[(20 * 80 + 60) * 4] > 200 && svgPx.Bgra[(20 * 80 + 60) * 4 + 2] < 50;
            Check("SVG の絵が描かれている（赤い四角・青い丸）", red && blue);
            Check("元の SVG は書き換えない", File.ReadAllText(svg).Contains("#FF0000"));

            // ---- 2. 4 隅で変形して常点灯画像として ----
            var front = Path.Combine(workDir, "src", "monitor.png");
            var mpx = new byte[64 * 48 * 4];
            for (int i = 0; i < mpx.Length; i += 4) { mpx[i] = 200; mpx[i + 1] = 160; mpx[i + 2] = 20; mpx[i + 3] = 255; }
            File.WriteAllBytes(front, PngWriter.Encode(64, 48, mpx));
            Point[] quad = [new(210, 110), new(320, 125), new(318, 215), new(205, 222)];
            WarpKindAnswer = () => 0;
            WarpAnswer = w =>
            {
                w.Show();
                w.SetCornersForTest(quad);
                w.UpdateLayout();
                SaveWindowPng(w, stem + "_warp.png");
                w.Close();
                return true;
            };
            before = _doc.Sections.Count;
            WarpImport(front, null, null); // メニューから（種類を聞く）
            await Task.Delay(200);
            var wsec = _doc.Sections.Count == before + 1 ? _doc.Sections[^1] : null;
            Check($"変形した常点灯画像を置く（{wsec?.Get("DaytimeImage")}・{wsec?.Get("Location")}）", wsec?.Kind == SectionKind.PilotLamp && wsec.Get("Location") == "205, 110");
            var wpx = wsec != null ? ImageLoader.TryLoadPixels(Path.Combine(workDir, wsec.Get("DaytimeImage")!)) : null;
            Check($"画像は 2 の累乗の大きさ（{wpx?.Width}×{wpx?.Height}）", wpx is { Width: 128, Height: 128 });
            // 左上の隅（205, 110）の外側（四角の左上の角のあたり）は透明、真ん中は不透明
            Check("4 隅の外は透明、中は不透明", wpx != null && wpx.Bgra[(1 * 128 + 1) * 4 + 3] == 0 && wpx.Bgra[(55 * 128 + 55) * 4 + 3] == 255);
            Check($"書き出した画像の名前を変える（{wsec?.Get("DaytimeImage")}）", wsec?.Get("DaytimeImage") == "warp\\monitor_warp.png");
            Check($"変形の方法を覚える（{(wsec?.Warp is { } wi ? wi.ToText() : "なし")}）",
                wsec?.Warp is { } wi2 && wi2.Source == "src\\monitor.png" && wi2.Corners[2] == (318, 215) && wi2.Interval == 0 && wi2.SvgScale == null);
            Check("パネルファイルに「;@変形:」で書く", Editor.Text.Contains(";@変形: 元=src\\monitor.png | 隅=210,110 320,125 318,215 205,222"));
            int wIndex = _doc.Sections.Count - 1;
            Check("一覧に「変形」の印を出す", ElementList.Items.OfType<ElementRow>().Any(r => r.Index == wIndex && !r.IsHeader && r.WarpVisibility == Visibility.Visible)
                && ElementList.Items.OfType<ElementRow>().Where(r => r.Index != wIndex && !r.IsHeader).All(r => r.WarpVisibility != Visibility.Visible));

            // ---- 2b. 画像を変える: 覚えた 4 隅で変形し直す ----
            var front2 = Path.Combine(workDir, "src", "monitor2.png");
            var m2 = new byte[32 * 24 * 4];
            for (int i = 0; i < m2.Length; i += 4) { m2[i] = 30; m2[i + 1] = 30; m2[i + 2] = 230; m2[i + 3] = 255; }
            File.WriteAllBytes(front2, PngWriter.Encode(32, 24, m2));
            ApplySelection(wIndex, [wIndex], null, SelectionSource.Other);
            WarpPickAnswer = () => front2;
            OnWarpChangeImage();
            await Task.Delay(200);
            var csec = _doc.Sections[wIndex];
            Check($"画像を変えると、同じ 4 隅で変形し直す（{csec.Get("DaytimeImage")}・{csec.Get("Location")}）",
                csec.Get("DaytimeImage") == "warp\\monitor2_warp.png" && csec.Get("Location") == "205, 110" && csec.Warp?.Source == "src\\monitor2.png" && csec.Warp.Corners[1] == (320, 125));
            var cpx = ImageLoader.TryLoadPixels(Path.Combine(workDir, csec.Get("DaytimeImage")!));
            Check("変形し直した絵は新しい画像の色", cpx != null && cpx.Bgra[(55 * 128 + 55) * 4 + 2] > 200 && cpx.Bgra[(55 * 128 + 55) * 4] < 60);
            Check("前の変形の画像は残す（元に戻せるように）", File.Exists(Path.Combine(workDir, "warp", "monitor_warp.png")));

            // ---- 2c. 4 隅を直す: 覚えた元の画像で、4 隅を決め直す ----
            Point[]? shownCorners = null;
            WarpAnswer = w =>
            {
                shownCorners = w.Corners.ToArray();
                w.SetCornersForTest([new(150, 60), new(260, 70), new(260, 160), new(150, 150)]);
                return true;
            };
            OnWarpEditCorners();
            await Task.Delay(200);
            var esec = _doc.Sections[wIndex];
            Check($"4 隅を直す画面は今の 4 隅から始まる（{(shownCorners != null ? string.Join(" ", shownCorners) : "")}）", shownCorners != null && shownCorners[3] == new Point(205, 222));
            Check($"4 隅を直すと位置と画像が変わる（{esec.Get("DaytimeImage")}・{esec.Get("Location")}）",
                esec.Get("Location") == "150, 60" && esec.Get("DaytimeImage") == "warp\\monitor2_warp_2.png" && esec.Warp is { } ew && ew.Corners[0] == (150, 60) && ew.Source == "src\\monitor2.png");
            Editor.Undo();
            FlushReparse();
            Check("元に戻すで 4 隅を直す前に戻る", _doc.Sections[wIndex].Get("Location") == "205, 110" && _doc.Sections[wIndex].Warp?.Corners[0] == (210, 110));
            WarpPickAnswer = null;

            // ---- 3. ドロップして「DigitalNumber（コマ割済）として 4 隅で変形して貼りこむ」（10 コマ） ----
            var digits = Path.Combine(workDir, "src", "digits.png");
            var dpx = new byte[20 * 100 * 4];
            for (int f = 0; f < 10; f++)
                for (int y = f * 10 + 2; y < f * 10 + 8; y++)
                    for (int x = 2; x < 4 + f; x++) { int o = (y * 20 + x) * 4; dpx[o] = dpx[o + 1] = dpx[o + 2] = 240; dpx[o + 3] = 255; }
            File.WriteAllBytes(digits, PngWriter.Encode(20, 100, dpx));
            WarpKindAnswer = () => { log.Add("     （ドロップでは種類を聞き直さないはず）"); return -1; };
            string? dropItems = null;
            ChoiceDialog.TestAnswer = (_, items) => { dropItems = string.Join(" | ", items); return items.Count - 1; };
            int? guessed = null;
            WarpIntervalAnswer = g => { guessed = g; return "10"; };
            Point? startCenter = null;
            WarpAnswer = w =>
            {
                startCenter = new Point((w.Corners[0].X + w.Corners[2].X) / 2, (w.Corners[0].Y + w.Corners[2].Y) / 2);
                w.SetCornersForTest([new(400, 300), new(440, 304), new(440, 324), new(400, 320)]);
                return true;
            };
            before = _doc.Sections.Count;
            HandleDrop([digits], new Point(420, 312));
            await Task.Delay(200);
            Check($"ドロップの選択肢に 4 隅の変形がある（{dropItems?.Split(" | ").Length} 個）",
                dropItems != null && dropItems.Contains("常点灯画像として 4 隅で変形して貼りこむ") && dropItems.Contains("DigitalNumber（コマ割済）として 4 隅で変形して貼りこむ"));
            Check($"落とした所を中心に変形の画面を開く（{startCenter}）", startCenter is { } sc && Math.Abs(sc.X - 420) <= 1 && Math.Abs(sc.Y - 312) <= 1);
            var dsec = _doc.Sections.Count == before + 1 ? _doc.Sections[^1] : null;
            Check($"1 コマの高さを推定して聞く（{guessed}）", guessed == 10);
            Check($"変形したデジタル数字を置く（Interval {dsec?.Get("Interval")}・{dsec?.Get("Location")}）",
                dsec?.Kind == SectionKind.DigitalNumber && dsec.Get("Interval") == "24" && dsec.Get("Location") == "400, 300");
            var dimg = dsec != null ? ImageLoader.TryLoadPixels(Path.Combine(workDir, dsec.Get("DaytimeImage")!)) : null;
            Check($"コマを縦に並べ直した画像（{dimg?.Width}×{dimg?.Height}、10 コマ × 24 を 2 の累乗に）", dimg is { Width: 64, Height: 256 });
            Check($"デジタル数字も変形の方法を覚える（{dsec?.Get("DaytimeImage")}・{dsec?.Warp?.ToText()}）", dsec?.Get("DaytimeImage") == "warp\\digits_warp.png" && dsec.Warp is { Interval: 10, Source: "src\\digits.png" });

            // ---- 4. パネルファイルの一部を取り込む（モニタくんの書き出しの形。画像は img フォルダ） ----
            // パネルのフォルダの外（モニタくんの書き出し先）に置く
            var outDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workDir)) + "_monitor_out";
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            Directory.CreateDirectory(Path.Combine(outDir, "img"));
            File.WriteAllBytes(Path.Combine(outDir, "img", "clock.png"), PngWriter.Encode(32, 32, new byte[32 * 32 * 4]));
            File.WriteAllBytes(Path.Combine(outDir, "img", "next.png"), PngWriter.Encode(64, 16, Enumerable.Repeat((byte)255, 64 * 16 * 4).ToArray()));
            var fragFile = Path.Combine(outDir, "monitor_panel.txt");
            File.WriteAllText(fragFile, """
                ; モニタくん で書き出したモニタの部分

                ;===== グループ: モニタ =====
                ;時計
                [DigitalNumber]
                Subject = hour
                Location = 220, 120
                DaytimeImage = img\clock.png
                Interval = 16
                Layer = 3

                ;===== グループ: 文字 =====
                ;次駅
                [PilotLamp]
                Subject = ats20
                Location = 230, 150
                DaytimeImage = img\next.png
                Layer = 3
                ;===== グループ終わり: 文字 =====
                ;===== グループ終わり: モニタ =====
                """, new System.Text.UTF8Encoding(true));
            before = _doc.Sections.Count;
            ImportFragment(fragFile);
            await Task.Delay(300);
            var added = _doc.Sections.Skip(before).ToList();
            Check($"取り込んだオブジェクト（{added.Count} 個）", added.Count == 2);
            Check($"全体を 1 つのグループに（{string.Join(" / ", added.Select(s => string.Join(">", s.Groups)))}）",
                added.Count == 2 && added[0].Groups.SequenceEqual(["monitor_panel", "モニタ"]) && added[1].Groups.SequenceEqual(["monitor_panel", "モニタ", "文字"]));
            Check($"画像をパネルのフォルダに写して指す（{added.FirstOrDefault()?.Get("DaytimeImage")}）",
                added.Count == 2 && added[0].Get("DaytimeImage") == "img\\clock.png" && File.Exists(Path.Combine(workDir, "img", "clock.png")) && File.Exists(Path.Combine(workDir, "img", "next.png")));
            Check("取り込んだグループを選んでいる", _selGroup == "monitor_panel");
            // 同じものをもう一度: グループ名は重ならないように、画像は同じ中身なので写し直さない
            ImportFragment(fragFile);
            await Task.Delay(200);
            var again = _doc.Sections.Skip(before + 2).ToList();
            Check($"2 回目はグループ名を変える（{again.FirstOrDefault()?.Groups.FirstOrDefault()}）", again.Count == 2 && again[0].Groups[0] == "monitor_panel 2" && again[0].Groups[1] != "モニタ");
            Check("同じ画像は写し直さない", again.Count == 2 && again[0].Get("DaytimeImage") == "img\\clock.png" && !File.Exists(Path.Combine(workDir, "img", "clock_2.png")));
            // ファイルに書いて読み直してもグループが保たれる
            var re = PanelDocument.Parse(_doc.Serialize());
            Check("書き出して読み直してもグループが保たれる", re.GroupWarnings.Count == 0 && re.Sections[^1].Groups.SequenceEqual(again[1].Groups));
            // 元に戻すで取り込む前に戻る
            Editor.Undo();
            FlushReparse();
            Check("元に戻すで 2 回目の取り込みを戻せる", _doc.Sections.Count == before + 2);

            // ---- 5. [This] のあるパネルファイルは取り込まない（Core で判定） ----
            Check("[This] のあるパネルは取り込まない", PanelFragment.HasThis(PanelFragment.Parse(File.ReadAllText(panel))));
            Check(".monitakun はモニタくんの作業ファイルとして見分ける", IsMonitaKunFile("a.MonitaKun") && !IsMonitaKunFile("a.txt"));

            ApplySelection(wIndex, [wIndex], null, SelectionSource.Other); // 画面の保存用: 変形したオブジェクトを選ぶ
            Canvas.FitAll();
            await Task.Delay(300);
            SaveWindowPng(this, png);
        }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); fail++; }
        finally
        {
            ChoiceDialog.TestAnswer = null;
            SvgScaleAnswer = null;
            WarpAnswer = null; WarpKindAnswer = null; WarpIntervalAnswer = null; WarpPickAnswer = null;
        }
        log.Add(fail == 0 ? "ALL PASSED" : $"{fail} FAILED");
        File.WriteAllLines(report, log);
        _savedText = Editor.Text;
        Close();
    }
}
