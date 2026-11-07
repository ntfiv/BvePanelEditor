using System.IO;
using BvePanelEditor.Core;
using BvePanelEditor.Core.Crop;
using BvePanelEditor.Views;

namespace BvePanelEditor;

/// <summary>画像からパネルを作る機能の通し確認（--croptest 最初の画像 作業フォルダ 結果.txt）。元の画像は読むだけ。</summary>
public partial class MainWindow
{
    public async void RunCropTest(string firstImage, string workDir, string reportPath)
    {
        var log = new List<string>();
        int fail = 0;
        void Check(string name, bool ok)
        {
            log.Add($"{(ok ? "OK  " : "FAIL")} {name}");
            if (!ok) fail++;
        }
        try
        {
            Directory.CreateDirectory(workDir);
            _autoConfirm = _ => true;
            var guess = ImagePanelWindow.Guess(firstImage);
            log.Add("     名前から探した 4 点: " + string.Join(" / ", guess.Select(g => g != null ? Path.GetFileName(g) : "なし")));
            Check("4 点が名前から見つかる", guess.All(g => g != null));

            var w = new ImagePanelWindow(this, workDir, firstImage, 5);
            w.Show();
            await Task.Delay(1500);
            Check("既定は感度 5・まとめる距離 2", w.DetectSettingsForTest == (5, 2));
            int found = w.Detect(replace: true);
            log.Add($"     自動で探した囲み（夜の差、既定の感度 5、まとめる距離 2）: {found} 個");
            Check("4 点の画像では感度を上げない（5 のまま）", w.DetectSettingsForTest.Threshold == 5 && w.DetectNoteForTest.Length == 0);
            Check("自動で囲みが見つかる", found >= 5);
            // すべて消す → Ctrl+Z で戻す → Ctrl+Y でやり直す → もう一度戻す
            int n0 = w.Regions.Count;
            w.DeleteAllRegions();
            Check("すべて消す", w.Regions.Count == 0);
            Check("Ctrl+Z で元に戻す", w.UndoRegions() && w.Regions.Count == n0);
            Check("Ctrl+Y でやり直す", w.RedoRegions() && w.Regions.Count == 0);
            w.UndoRegions();
            Check("もう一度戻すと元の数", w.Regions.Count == n0);
            foreach (var r in w.Regions) log.Add($"       {r.Name}: {r.Rect.Left},{r.Rect.Top} {r.Rect.Width}×{r.Rect.Height}");

            // 横に長い囲み（横並びの灯がまとまったもの）を分ける
            var wide = w.Regions.Where(r => r.Rect.Width > r.Rect.Height * 3 && r.Rect.Width > 60).ToList();
            log.Add($"     横に長い囲み: {wide.Count} 個");
            if (wide.Count > 0)
            {
                int before = w.Regions.Count;
                w.SelectForTest(wide);
                w.SplitSelected();
                log.Add($"     分けた後の囲み: {w.Regions.Count} 個（{before} 個から）");
                Check("「分ける」で横並びの灯が分かれる", w.Regions.Count > before);
            }
            // 名前と状態量を少し付ける（残りは空のまま）
            w.Regions[0].Subject = "ats0";
            w.Regions[0].Name = "最初の灯";
            w.SelectForTest([w.Regions[0]]);
            await Task.Delay(300);
            SaveWindowPng(w, Path.ChangeExtension(reportPath, ".window.png"));
            var spec = w.MakeSpecForTest()!;
            w.Close();

            var panelPath = Path.Combine(workDir, "panel.txt");
            Check("新しいパネルを作れる", ApplyCropBuild(spec, panelPath, interactive: false));
            var lamps = _doc!.Sections.Where(s => s.Kind == SectionKind.PilotLamp && !s.IsMainPanel).ToList();
            Check("囲みの数だけ表示灯ができる", lamps.Count == spec.Regions.Count);
            string Full(string rel) => Path.GetFullPath(Path.Combine(workDir, rel));
            Check("[This] は画像なし（透明な画像は書き出さない）", _doc!.This?.Get("DaytimeImage") == null && !Directory.EnumerateFiles(PanelDir!, "this_blank_*").Any() && _doc.This?.Get("NighttimeImage") == null);
            Check("メインパネルは元の昼・無発光の画像を指す（レイヤー 1）", _doc.MainPanel?.Get("DaytimeImage") is { } bd && string.Equals(Full(bd), Path.GetFullPath(spec.UnlitDayPath), StringComparison.OrdinalIgnoreCase)
                && _doc.MainPanel.Get("Layer") == "1");
            Check("メインパネルの夜は元の夜・無発光の画像を指す", _doc.MainPanel?.Get("NighttimeImage") is { } bn && string.Equals(Full(bn), Path.GetFullPath(spec.UnlitNightPath!), StringComparison.OrdinalIgnoreCase));
            Check("表示灯はメインパネルより上", lamps.All(s => PanelValues.Double(s.Get("Layer"), 0) > 1));
            Check("表示灯の昼・夜の画像がある", lamps.All(s => File.Exists(Full(s.Get("DaytimeImage")!)) && File.Exists(Full(s.Get("NighttimeImage")!))));
            Check("名前と状態量が入る", lamps[0].Title == "最初の灯" && lamps[0].Get("Subject") == "ats0");
            Check("つながりのコメントが入る", Editor.Text.Contains(";===== 切り抜き元 発光: 昼=") && Editor.Text.Contains(";@切り抜き: 発光"));
            Check("画像の問題はない", !_scene!.Problems.Any(p => p.Message.Contains("画像")));

            Check("保存できる", Save(_path));
            int count = _doc.Sections.Count;
            LoadPanel(_path!, null);
            Check("開き直してもつながりが残る", _doc!.HasCropLink && _doc.Sections.Count == count);
            // 元の画像を開いたとき、作ったパネルが見つかる（作り直して上書きしないよう、パネルを開くか聞くため）
            var foundPanels = BvePanelEditor.Core.Crop.CropPanelFinder.Find(firstImage, [_path!]);
            Check("元の画像から、作ったパネルが見つかる", foundPanels.Any(p => string.Equals(p, _path, StringComparison.OrdinalIgnoreCase)));
            foreach (var g in guess.Where(g => g != null))
            {
                var f = BvePanelEditor.Core.Crop.CropPanelFinder.Find(g!, [_path!]);
                log.Add($"     「{Path.GetFileName(g)}」を使っているパネル: {string.Join("、", f.Select(Path.GetFileName))}");
                Check($"「{Path.GetFileName(g)}」からも見つかる", f.Any(p => string.Equals(p, _path, StringComparison.OrdinalIgnoreCase)));
            }

            var t = Editor.Text;
            var lampImg = Full(_doc.Sections.First(s => s.CropSet != null).Get("DaytimeImage")!);
            var before2 = File.ReadAllBytes(lampImg);
            var plan = RunCropUpdate(interactive: false);
            Check("切り抜き元から更新できる", plan != null && plan.Missing.Count == 0 && plan.Skipped.Count == 0 && plan.Files.Count == lamps.Count * 2);
            Check("すぐ更新しても中身とパネルは変わらない", Editor.Text == t && File.ReadAllBytes(lampImg).AsSpan().SequenceEqual(before2));

            // 動作プレビュー: 夜で全部点灯した見え方を保存
            NightSlider.Value = 1;
            AllLampsCheck.IsChecked = true;
            OnPreviewOption(this, new System.Windows.RoutedEventArgs());
            Canvas.FitAll();
            UpdateLayout();
            await Task.Delay(500);
            SaveWindowPng(this, Path.ChangeExtension(reportPath, ".png"));
        }
        catch (Exception ex)
        {
            log.Add("EXCEPTION " + ex);
            fail++;
        }
        _autoConfirm = null;
        log.Add(fail == 0 ? "ALL PASSED" : $"{fail} FAILED");
        File.WriteAllLines(reportPath, log);
        _savedText = Editor.Text;
        Close();
    }
}
