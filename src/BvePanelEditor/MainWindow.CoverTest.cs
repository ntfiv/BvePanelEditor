using System.IO;
using System.Windows;
using BvePanelEditor.Core;
using BvePanelEditor.Core.Crop;
using BvePanelEditor.Rendering;
using BvePanelEditor.Views;

namespace BvePanelEditor;

/// <summary>
/// 確認用: 指針のふたを作る（--covertest 画面.png 結果.txt）。既存のパネル（[This] に背景・パネル本体は表示灯）のコピーを開いて使う（保存する）。
/// 速度計の針のふたを、パネル本体の画像から丸く切り抜き、手作りの Spd_cov.png（461,1642 の 64px、丸の半径 約 25）と比べる。
/// </summary>
public partial class MainWindow
{
    public async void RunCoverTest(string png, string report)
    {
        var log = new List<string>();
        int fail = 0;
        void Check(string name, bool ok) { log.Add($"{(ok ? "OK  " : "FAIL")} {name}"); if (!ok) fail++; }
        try
        {
            var doc = _doc!;
            int speed = doc.Sections.FindIndex(s => s.Kind == SectionKind.Needle && s.Get("Subject") == "kmph");
            int bc = doc.Sections.FindIndex(s => s.Kind == SectionKind.Needle && s.Get("Subject") == "bc");
            SelectForStartup(doc.Sections.IndexOf(doc.This!));
            Check("[This] を選んでいるときは「ふたを作る」ボタンを出さない", CoverButton.Visibility != Visibility.Visible);
            SelectForStartup(speed);
            Check("指針を選ぶと「ふたを作る」ボタンが出る", CoverButton.Visibility == Visibility.Visible);

            // 速度計
            var targets = CoverTargets();
            Check("速度計の針は 1 本", targets.SequenceEqual([speed]));
            var sources = CoverSources(targets);
            log.Add("     切り抜く元の候補: " + string.Join(" ／ ", sources.Select(s => s.Label)));
            Check("いちばん上の候補はパネル本体（_base\\Panel.png）", sources.Count > 0 && sources[0].DayPath.EndsWith(Path.Combine("_base", "Panel.png")) && sources[0].NightPath != null);
            var needles = targets.Select(i => _scene!.ItemFor(i)!).ToList();
            var w = new CoverWindow(this, sources, needles, DefaultCoverName(targets), null);
            w.Show();
            await Task.Delay(800);
            var res = w.ResultForTest();
            var c = res.Shapes.FirstOrDefault(s => s.IsCircle);
            log.Add($"     丸: 中心 {c?.Cx},{c?.Cy} 半径 {c?.R}（手作り: 中心 492.5,1673.5 半径 約 25）・候補 {w.CandidateCountForTest} 個");
            Check("開くと軸のふたの丸を自動で置く（手作りのふたと中心 1.5px・半径 1.5px 以内）",
                c != null && Math.Abs(c.Cx - 492.5) <= 1.5 && Math.Abs(c.Cy - 1673.5) <= 1.5 && Math.Abs(c.R - 25) <= 1.5);
            Check("自動で探すボタンの名前は「切り抜きの自動検出」", w.AutoLabelForTest == "切り抜きの自動検出");
            SaveWindowPng(w, Path.ChangeExtension(png, ".window.png"));
            w.Close();

            int before = doc.Sections.Count;
            Check("既定の名前は「cover_名前」（スペースなし）", res.Name == "cover" || (res.Name.StartsWith("cover_") && !res.Name.Contains(' ')));
            log.Add($"     既定の名前: {res.Name}");
            doc.Sections[speed].SetTitle("速度計");
            Check("名前のある針は「cover_速度計」", DefaultCoverName(targets) == "cover_速度計");
            doc.Sections[speed].SetTitle("時計 時針");
            Check("名前のスペースも「_」にする（cover_時計_時針）", DefaultCoverName(targets) == "cover_時計_時針");
            doc.Sections[speed].SetTitle("");
            int at = PlaceCover(res with { Name = "cover 速度計" }, targets);
            var cov = doc.Sections[at];
            Check("ふたを 1 つ足し、針のすぐ後ろに入れる", doc.Sections.Count == before + 1 && at == speed + 1);
            Check("常表示・針より 1 つ上のレイヤー（9）", cov.Get("Subject") == "true" && cov.Get("Layer") == "9");
            var dayFile = Path.Combine(PanelDir!, cov.Get("DaytimeImage")!);
            var nightFile = Path.Combine(PanelDir!, cov.Get("NighttimeImage") ?? "-");
            Check("ファイル名にスペースを入れない（cover_速度計.png・cover_速度計_N.png）", Path.GetFileName(cov.Get("DaytimeImage")) == "cover_速度計.png" && Path.GetFileName(cov.Get("NighttimeImage")) == "cover_速度計_N.png");
            Check("crop フォルダに昼・夜の画像を書き出す", File.Exists(dayFile) && File.Exists(nightFile) && Path.GetFileName(Path.GetDirectoryName(dayFile)) == "crop");
            var px = ImageLoader.TryLoadPixels(dayFile)!;
            Check("2 の累乗の大きさ（64×64）", px.Width == 64 && px.Height == 64);
            Check("丸の外（角）は透明・中は不透明", px.Bgra[3] == 0 && px.Bgra[(25 * px.Width + 25) * 4 + 3] == 255);
            Check("切り抜き元とつなぎ、形を覚える", cov.CropSet != null && cov.CropShapes.Count == 1 && doc.CropSets.Any(s => s.Name == cov.CropSet));
            log.Add($"     ふた: Location {cov.Get("Location")}（手作り 461, 1642）・組「{cov.CropSet}」・{PanelDocument.CropShapeLine(cov.CropShapes[0])}");
            Check("置いたふただけは選んでいる間動かせる", Canvas.PositionLocked && !Canvas.EditLocked);

            // 圧力計: 同じ位置の 2 本にまとめてかぶせる
            SelectForStartup(doc.Sections.FindIndex(s => s.Kind == SectionKind.Needle && s.Get("Subject") == "bc"));
            var pt = CoverTargets();
            Check("圧力計は同じ位置の 2 本（bc・mr）をまとめる", pt.Count == 2);
            var ps = CoverSources(pt);
            var pw = new CoverWindow(this, ps, pt.Select(i => _scene!.ItemFor(i)!).ToList(), "cover_圧力計", null);
            pw.Show();
            await Task.Delay(800);
            log.Add($"     圧力計の丸の候補 {pw.CandidateCountForTest} 個");
            Check("圧力計は丸の候補が複数（次の候補で選び直せる）", pw.CandidateCountForTest >= 2);
            // 多角形も足して（変わった形のふた）切り抜けるか
            var pc = pw.ResultForTest().Shapes[0];
            pw.AddPolygonForTest([(pc.Cx - 10, pc.Cy), (pc.Cx + 10, pc.Cy), (pc.Cx + 10, pc.Cy + 30), (pc.Cx - 10, pc.Cy + 30)]);
            pw.AddEllipseForTest(pc.Cx, pc.Cy - 20, 18, 8);
            await Task.Delay(300);
            var pres = pw.ResultForTest();
            SaveWindowPng(pw, Path.ChangeExtension(png, ".window2.png"));
            pw.Close();
            int pat = PlaceCover(pres, pt);
            var pcov = doc.Sections[pat];
            Check("圧力計のふたは 2 本のどちらよりも上（レイヤー 9）・丸＋多角形＋楕円", pcov.Get("Layer") == "9" && pcov.CropShapes.Count == 3 && pcov.CropShapes.Count(s => s.IsEllipse) == 1);
            log.Add("     " + string.Join(" ／ ", pcov.CropShapes.Select(PanelDocument.CropShapeLine)));

            // 保存して開き直し、切り抜き元から更新しても変わらない
            Check("保存できる", Save(_path));
            LoadPanel(_path!, _vehicle);
            var back = _doc!.Sections.Where(s => s.Title.StartsWith("cover")).ToList();
            Check("開き直しても形を覚えている", back.Count == 2 && back.All(s => s.CropShapes.Count > 0 && s.CropSet != null));
            var plan = CropUpdate.Plan(_doc, PanelDir!, ImageLoader.TryLoadPixels);
            var mine = plan.Files.Where(f => Path.GetFileName(Path.GetDirectoryName(f.FullPath)) == "crop").ToList();
            Check("切り抜き元から更新しても、ふたの画像は変わらない", mine.Count == 4 && mine.All(f => File.ReadAllBytes(f.FullPath).AsSpan().SequenceEqual(f.Png)) && plan.Missing.Count == 0);
            if (plan.Missing.Count > 0) log.Add("     " + string.Join(" / ", plan.Missing));

            // パネル画面: 80 km/h で針がふたの下に隠れる
            Canvas.State["kmph"] = 80; Canvas.State["bc"] = 400; Canvas.State["mr"] = 800;
            await Task.Delay(800); // 開き直したあとの「全体を表示」が済んでから拡大する
            Canvas.ZoomCenter(2.5);
            Canvas.CenterOn(new Point(640, 1673));
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
}
