using System.IO;
using System.Windows;
using BvePanelEditor.Core;
using BvePanelEditor.Core.Psd;
using BvePanelEditor.Views;

namespace BvePanelEditor;

/// <summary>
/// 確認用: パネルを開いているときに画像を読み込む（常点灯画像／デジタル数字のコマ割済・単コマ）（--digitimporttest 作業フォルダ 画面.png 結果.txt）。
/// 作業フォルダに確認用のパネルと画像を作って使う。
/// </summary>
public partial class MainWindow
{
    public async void RunDigitImportTest(string workDir, string png, string report)
    {
        var log = new List<string>();
        int fail = 0;
        void Check(string name, bool ok) { log.Add($"{(ok ? "OK  " : "FAIL")} {name}"); if (!ok) fail++; }
        try
        {
            Directory.CreateDirectory(workDir);
            var src = Path.Combine(workDir, "src");
            Directory.CreateDirectory(src);
            // パネル（512×512 の灰色）
            var gray = new byte[512 * 512 * 4];
            for (int i = 0; i < gray.Length; i += 4) { gray[i] = gray[i + 1] = gray[i + 2] = 90; gray[i + 3] = 255; }
            File.WriteAllBytes(Path.Combine(workDir, "base.png"), PngWriter.Encode(512, 512, gray));
            var panel = Path.Combine(workDir, "panel.txt");
            File.WriteAllText(panel, "BveTs Instrument Panel 2.00\r\n\r\n[This]\r\nResolution = 512\r\nDaytimeImage = base.png\r\n");
            LoadPanel(panel, null);
            await Task.Delay(300);

            // コマ割済の画像（20×100・10 コマ。2 の累乗ではない）。コマ i には幅 4+i の白い棒
            byte[] Frames(int w, int h, int interval)
            {
                var px = new byte[w * h * 4];
                for (int f = 0; f < h / interval; f++)
                    for (int y = 2; y < interval - 2; y++)
                        for (int x = 2; x < 4 + f && x < w; x++) { int o = ((f * interval + y) * w + x) * 4; px[o] = px[o + 1] = px[o + 2] = 240; px[o + 3] = 255; }
                return px;
            }
            var framed = Path.Combine(src, "speed.png");
            File.WriteAllBytes(framed, PngWriter.Encode(20, 100, Frames(20, 100, 10)));
            var framedBytes = File.ReadAllBytes(framed);
            // 単コマの画像（3 枚。名前は数の順になるように d1・d2・d10）
            string One(string name, int w, int h, byte v)
            {
                var px = new byte[w * h * 4];
                for (int i = 0; i < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255; }
                var p = Path.Combine(src, name);
                File.WriteAllBytes(p, PngWriter.Encode(w, h, px));
                return p;
            }
            var d10 = One("d10.png", 10, 8, 200);
            var d1 = One("d1.png", 12, 10, 100);
            var d2 = One("d2.png", 12, 10, 150);

            var asked = new List<string>();
            int Answer(string msg, IReadOnlyList<string> items, int read, int size, int transparent)
            {
                asked.Add(msg);
                if (msg.Contains("どう読み込みますか")) return read;
                if (msg.Contains("2 の累乗")) return size;
                if (msg.Contains("透明なコマ")) return transparent;
                return -1;
            }

            // ---- 選択肢 ----
            string? first = null;
            ChoiceDialog.TestAnswer = (msg, items) => { first ??= string.Join(" | ", items); return -1; };
            HandleDrop([framed], new Point(100, 100));
            log.Add("     選択肢: " + first);
            Check("選択肢は「常点灯画像」「DigitalNumber（コマ割済）」「DigitalNumber（単コマ）」の 3 つ", first != null && first.Split(" | ").Length == 3
                && first.Contains("常点灯画像として読み込む") && first.Contains("（コマ割済）") && first.Contains("（単コマ）"));

            // ---- コマ割済: 大きさを直す → テスト画面 ----
            DigitTestWindow? seen = null;
            int seenInterval = 0, seenFrames = 0;
            DigitTestWindow.TestAnswer = w =>
            {
                seen = w; seenInterval = w.Interval; seenFrames = w.FrameCount;
                w.Value = 3;
                w.Show(); w.UpdateLayout();
                SaveWindowPng(w, Path.ChangeExtension(png, ".test.png"));
                w.Close();
                return true;
            };
            ChoiceDialog.TestAnswer = (m, i) => Answer(m, i, 1, 0, 0);
            int before = _doc!.Sections.Count;
            HandleDrop([framed], new Point(100, 100));
            var dn = _doc.Sections[^1];
            log.Add($"     コマ割済: Interval {dn.Get("Interval")}・状態量 {dn.Get("Subject")}・画像 {dn.Get("DaytimeImage")}");
            Check("2 の累乗でなければ、直すか聞く", asked.Any(a => a.Contains("20×100") && a.Contains("32×128")));
            Check("テスト画面を出し、1 コマの高さは画像から求める（10・10 コマ）", seen != null && seenInterval == 10 && seenFrames == 10);
            Check("デジタル数字として置く（Interval 10・状態量 ats99）", _doc.Sections.Count == before + 1 && dn.Kind == SectionKind.DigitalNumber
                && dn.Get("Interval") == "10" && dn.Get("Subject") == DigitDefaultSubject);
            var outFile = Path.Combine(PanelDir!, dn.Get("DaytimeImage") ?? "");
            Check("直した画像（左上に詰めて透明で埋めた 32×128）を digit フォルダに書き出す", outFile.Contains($"{Path.DirectorySeparatorChar}digit{Path.DirectorySeparatorChar}")
                && PngWriter.TryReadSize(outFile, out var ow, out var oh) && (ow, oh) == (32, 128)
                && PngWriter.TryReadContentSize(outFile, out var cw, out var chh) && (cw, chh) == (20, 100));
            Check("元の画像は書き換えない", File.ReadAllBytes(framed).AsSpan().SequenceEqual(framedBytes));

            // 「やめる」なら置かない
            asked.Clear();
            ChoiceDialog.TestAnswer = (m, i) => Answer(m, i, 1, 2, 0);
            before = _doc.Sections.Count;
            HandleDrop([framed], new Point(100, 100));
            Check("大きさの確認で「やめる」なら置かない", _doc.Sections.Count == before);
            // テスト画面でキャンセルしても置かない
            ChoiceDialog.TestAnswer = (m, i) => Answer(m, i, 1, 1, 0);
            DigitTestWindow.TestAnswer = _ => false;
            HandleDrop([framed], new Point(100, 100));
            Check("テスト画面でキャンセルなら置かない", _doc.Sections.Count == before);
            // 「このまま使う」なら元の画像（パネルのフォルダにコピー）
            DigitTestWindow.TestAnswer = w => { w.SetInterval(10); return true; };
            HandleDrop([framed], new Point(100, 100));
            Check("「このまま使う」なら元の画像をそのまま指す（パネルのフォルダの中なのでコピーしない）", _doc.Sections.Count == before + 1
                && _doc.Sections[^1].Get("DaytimeImage") == Path.Combine("src", "speed.png"));

            // ---- 単コマ: コマ割を作る画面 → 透明なコマ → テスト画面 ----
            List<string>? labels = null;
            DigitFramesWindow.TestAnswer = w =>
            {
                labels = w.LabelsForTest;
                w.Show(); w.UpdateLayout();
                SaveWindowPng(w, Path.ChangeExtension(png, ".frames.png"));
                w.Close();
                return true;
            };
            asked.Clear();
            ChoiceDialog.TestAnswer = (m, i) => Answer(m, i, 2, 0, 0);
            DigitTestWindow.TestAnswer = w => { seenInterval = w.Interval; seenFrames = w.FrameCount; return true; };
            before = _doc.Sections.Count;
            HandleDrop([d10, d2, d1], new Point(300, 300));
            log.Add("     コマ: " + string.Join(" ／ ", labels ?? []));
            Check("透明なコマがなければ、用意するか聞く", asked.Any(a => a.Contains("透明なコマ")));
            var sn = _doc.Sections[^1];
            var sn0Name = sn.Title;
            Check("単コマの画像は 1 枚 1 コマ・名前の数の順（d1・d2・d10）", labels is ["d1.png", "d2.png", "d10.png"] && sn0Name == "d");
            var snFile = Path.Combine(PanelDir!, sn.Get("DaytimeImage") ?? "");
            log.Add($"     単コマ: Interval {sn.Get("Interval")}・画像 {sn.Get("DaytimeImage")}・テスト画面 {seenInterval}／{seenFrames} コマ");
            Check("1 コマはいちばん大きいコマ（12×10）、先頭に透明なコマを足して 4 コマ", seenInterval == 10 && seenFrames == 4 && sn.Get("Interval") == "10");
            Check("できあがりは BVE のパネル用の大きさ（16×64）に広げて書き出す", PngWriter.TryReadSize(snFile, out var sw, out var sh) && (sw, sh) == (16, 64)
                && PngWriter.TryReadContentSize(snFile, out var scw, out var sch) && (scw, sch) == (12, 40));
            var strip = Rendering.ImageLoader.TryLoadPixels(snFile)!;
            Check("0 番は透明なコマ、1 番は d1、3 番は d10（左上に詰める）", DigitStrip.IsTransparent(DigitStrip.Frame(strip, 10, 0))
                && strip.Bgra[(10 * strip.Width + 0) * 4] == 100 && strip.Bgra[(30 * strip.Width + 0) * 4] == 200 && strip.Bgra[(30 * strip.Width + 11) * 4 + 3] == 0);

            // ---- 単コマ: 連番の画像を自動で読み込み、夜の画像を名前から割り当てる ----
            var seqDir = Path.Combine(src, "seq");
            Directory.CreateDirectory(seqDir);
            string SeqImg(string name, int w, int h, byte v)
            {
                var px = new byte[w * h * 4];
                for (int i = 0; i < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255; }
                var p = Path.Combine(seqDir, name);
                File.WriteAllBytes(p, PngWriter.Encode(w, h, px));
                return p;
            }
            for (int k = 0; k < 4; k++) SeqImg($"ID_motor_{k}.png", 12, 10, (byte)(50 + k * 40));
            SeqImg("ID_motor_0_N.png", 12, 12, 10);   // 夜は少し大きい（昼と夜で同じ大きさにそろえる）
            SeqImg("ID_motor_1N.png", 12, 10, 11);
            SeqImg("ID_motor_N_2.png", 12, 10, 12);   // 3 の夜はなし
            List<string?>? nightLabels = null;
            string? framesInfo = null;
            DigitFramesWindow.TestAnswer = w =>
            {
                labels = w.LabelsForTest; nightLabels = w.NightLabelsForTest; framesInfo = w.InfoForTest;
                w.ShowNightForTest(true);
                w.Show(); w.UpdateLayout();
                SaveWindowPng(w, Path.ChangeExtension(png, ".framesnight.png"));
                w.Close();
                return true;
            };
            PsdPixels? testNight = null;
            DigitTestWindow.TestAnswer = w => { seenInterval = w.Interval; seenFrames = w.FrameCount; testNight = w.Night; return true; };
            ChoiceDialog.TestAnswer = (m, i) => Answer(m, i, 2, 0, 2);   // 透明なコマは足さない
            before = _doc.Sections.Count;
            HandleDrop([Path.Combine(seqDir, "ID_motor_1.png")], new Point(250, 250));
            log.Add("     連番: " + string.Join(" ／ ", labels ?? []) + "　夜: " + string.Join(" ／ ", (nightLabels ?? []).Select(n => n ?? "なし")));
            Check("1 枚読むと、同じフォルダの連番の画像（ID_motor_0〜3）を自動で読み込む", labels is ["ID_motor_0.png", "ID_motor_1.png", "ID_motor_2.png", "ID_motor_3.png"]);
            Check("夜の画像は名前（_N・N・数の前の _N）から自動で割り当て、夜の画像そのものはコマにしない", nightLabels is ["ID_motor_0_N.png", "ID_motor_1N.png", "ID_motor_N_2.png", null]);
            var qn = _doc.Sections[^1];
            var qDay = Path.Combine(PanelDir!, qn.Get("DaytimeImage") ?? "");
            var qNight = Path.Combine(PanelDir!, qn.Get("NighttimeImage") ?? "");
            log.Add($"     連番の結果: Interval {qn.Get("Interval")}・昼 {qn.Get("DaytimeImage")}・夜 {qn.Get("NighttimeImage")}");
            Check("連番なら名前は末尾の数を外したもの（ID_motor）", qn.Title == "ID_motor");
            Check("昼と夜は同じ加工（1 コマ 12×12・4 コマ・16×64 に広げる）", _doc.Sections.Count == before + 1 && qn.Get("Interval") == "12" && seenFrames == 4 && testNight != null
                && PngWriter.TryReadSize(qDay, out var qw, out var qh) && PngWriter.TryReadSize(qNight, out var nw, out var nh) && (qw, qh) == (16, 64) && (nw, nh) == (16, 64)
                && PngWriter.TryReadContentSize(qNight, out var ncw, out var nch) && (ncw, nch) == (12, 48));
            var qnPx = Rendering.ImageLoader.TryLoadPixels(qNight)!;
            Check("夜の画像のないコマ（3 番）は昼の絵を使う", qnPx.Bgra[(36 * qnPx.Width) * 4] == 50 + 3 * 40 && qnPx.Bgra[(0 * qnPx.Width) * 4] == 10);

            // ---- コマ割済: 夜の画像を手で選ぶ（決まった名前でない夜の画像） ----
            var customNight = Path.Combine(src, "speed_yoru.png");
            File.WriteAllBytes(customNight, PngWriter.Encode(20, 100, Frames(20, 100, 10)));
            ChoiceDialog.TestAnswer = (m, i) => Answer(m, i, 1, 0, 0);
            DigitTestWindow.TestAnswer = w => w.SetNightFile(customNight);
            before = _doc.Sections.Count;
            HandleDrop([framed], new Point(100, 100));
            var cn = _doc.Sections[^1];
            var cnNight = Path.Combine(PanelDir!, cn.Get("NighttimeImage") ?? "");
            log.Add($"     コマ割済の夜: {cn.Get("NighttimeImage")}");
            Check("コマ割済でも夜の画像を選べ、昼と同じ加工（32×128）をする", _doc.Sections.Count == before + 1 && cn.Get("NighttimeImage") is { Length: > 0 }
                && PngWriter.TryReadSize(cnNight, out var cnw, out var cnh) && (cnw, cnh) == (32, 128));

            // ---- 単コマ: PSD（レイヤーを 1 コマずつ。見本の ID_motor_0-3.psd があれば） ----
            var samplePsd = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\sample\psd_to_panel\ID_motor_0-3.psd"));
            if (File.Exists(samplePsd))
            {
                var psdCopy = Path.Combine(src, "ID_motor_0-3.psd");
                File.Copy(samplePsd, psdCopy, true);
                int layerCount;
                using (var psd = PsdFile.Open(psdCopy)) layerCount = DigitStrip.FrameLayers(psd).Count;
                labels = null;
                DigitFramesWindow.TestAnswer = w =>
                {
                    labels = w.LabelsForTest;
                    w.Show(); w.UpdateLayout();
                    SaveWindowPng(w, Path.ChangeExtension(png, ".framespsd.png"));
                    w.Close();
                    return true;
                };
                ChoiceDialog.TestAnswer = (m, i) => Answer(m, i, 2, 0, 2);
                before = _doc.Sections.Count;
                HandleDrop([psdCopy], new Point(300, 200));
                log.Add($"     PSD の単コマ: レイヤー {layerCount} 枚 → コマ " + string.Join(" ／ ", labels ?? []) + $"・テスト画面 {seenInterval}／{seenFrames} コマ");
                Check("PSD はレイヤーを 1 コマずつ（レイヤーの数だけコマ）にして置く", labels != null && labels.Count == layerCount && layerCount > 1
                    && _doc.Sections.Count == before + 1 && seenFrames == layerCount);
            }
            else log.Add("     （見本の ID_motor_0-3.psd がないので、PSD の単コマは確かめていません）");

            // ---- コマ割済: PSD（レイヤーが複数ならレイヤーを選ぶ画面。見本の ID_motor.psd があれば） ----
            foreach (var psdName in new[] { "ID_motor.psd", "ID_motor_0-3.psd" })
            {
            var framedPsd = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\sample\psd_to_panel\" + psdName));
            if (File.Exists(framedPsd))
            {
                var copy = Path.Combine(src, "framed_" + psdName);
                File.Copy(framedPsd, copy, true);
                int composable;
                using (var psd = PsdFile.Open(copy)) composable = psd.ComposableLayers.Count();
                bool assignShown = false;
                LayerAssignAnswer = w => { assignShown = true; w.Show(); w.UpdateLayout(); SaveWindowPng(w, Path.ChangeExtension(png, ".assign." + psdName + ".png")); w.Close(); return true; };
                ChoiceDialog.TestAnswer = (m, i) => Answer(m, i, 1, 0, 0);
                DigitTestWindow.TestAnswer = w => { seenInterval = w.Interval; seenFrames = w.FrameCount; return true; };
                before = _doc.Sections.Count;
                HandleDrop([copy], new Point(200, 300));
                LayerAssignAnswer = null;
                var pd = _doc.Sections.Count > before ? _doc.Sections[^1] : null;
                log.Add($"     PSD のコマ割済: 使えるレイヤー {composable} 枚・レイヤーを選ぶ画面 {(assignShown ? "あり" : "なし")}・Interval {pd?.Get("Interval")}・画像 {pd?.Get("DaytimeImage")}");
                Check($"PSD のコマ割済（{psdName}）は、レイヤーが複数ならレイヤーを選ぶ画面を出し（1 枚ならそのまま）、PNG に書き出して置く", (assignShown == composable > 1) && pd is { Kind: SectionKind.DigitalNumber }
                    && File.Exists(Path.Combine(PanelDir!, pd.Get("DaytimeImage") ?? "")));
            }
            }
            // ---- 常点灯画像 ----
            ChoiceDialog.TestAnswer = (m, i) => Answer(m, i, 0, 0, 0);
            before = _doc.Sections.Count;
            HandleDrop([d1], new Point(400, 400));
            Check("常点灯画像は表示灯（Subject = true）として置く", _doc.Sections.Count == before + 1 && _doc.Sections[^1].Kind == SectionKind.PilotLamp && _doc.Sections[^1].Get("Subject") == "true");

            ChoiceDialog.TestAnswer = null;
            DigitTestWindow.TestAnswer = null;
            DigitFramesWindow.TestAnswer = null;
            Canvas.FitAll();
            UpdateLayout();
            await Task.Delay(300);
            SaveWindowPng(this, png);
        }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); fail++; }
        ChoiceDialog.TestAnswer = null;
        DigitTestWindow.TestAnswer = null;
        DigitFramesWindow.TestAnswer = null;
        log.Add(fail == 0 ? "ALL PASSED" : $"{fail} FAILED");
        File.WriteAllLines(report, log);
        _savedText = Editor.Text;
        Close();
    }
}
