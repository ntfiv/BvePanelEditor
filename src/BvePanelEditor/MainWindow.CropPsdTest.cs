using System.IO;
using BvePanelEditor.Core;
using BvePanelEditor.Core.Psd;
using BvePanelEditor.Views;

namespace BvePanelEditor;

/// <summary>
/// 画像からパネルを作るで PSD のレイヤーを振り分ける通し確認（--croppsdtest PSD 作業フォルダ 結果.txt）。PSD は読むだけ。
/// </summary>
public partial class MainWindow
{
    public async void RunCropPsdTest(string psdPath, string workDir, string reportPath)
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
            var psdBytes = File.ReadAllBytes(psdPath).Length;

            var w = new ImagePanelWindow(this, workDir, null, 5);
            w.Show();
            await Task.Delay(800);

            // 振り分ける画面（名前からの初期値）
            var aw = w.OpenAssignForTest(0, psdPath);
            aw.Show();
            await Task.Delay(2500);
            SaveWindowPng(aw, Path.ChangeExtension(reportPath, ".assign.png"));
            log.Add("     名前からの振り分け: " + aw.DescribeForTest());
            var picks = aw.PickForTest();
            aw.Close();
            Check("昼・無発光と昼・発光が振り分けられる", picks[0] != null && picks[1] != null);
            Check("夜・無発光と夜・発光が振り分けられる", picks[2] != null && picks[3] != null);
            for (int k = 0; k < 4; k++)
                if (picks[k] is { } p) log.Add($"       {ImagePanelWindow.SlotLabels[k]}: {string.Join(" | ", p.Layers)}");

            w.ApplyPicksForTest(picks);
            for (int k = 0; k < 4; k++) log.Add($"     欄 {ImagePanelWindow.SlotLabels[k]}: {w.SlotTextForTest(k)}");
            Check("欄に「レイヤー n 個を重ねる」と出る", w.SlotTextForTest(0).Contains("レイヤー"));
            await Task.Delay(500);
            log.Add("     " + w.DetectProbeForTest([5, 8, 12, 18, 24, 27, 40, 60], 2));
            // 既定（感度 5・まとめる距離 2）で探すと、全体が 1 つにまとまるので感度を自動で上げる
            int found = w.Detect(replace: true);
            log.Add($"     既定で探した囲み: {found} 個（感度 {w.DetectSettingsForTest.Threshold}・{w.DetectNoteForTest}）");
            Check("全体が 1 つにまとまる画像では、感度を自動で上げて探す（上げすぎない: 12 以下）", w.DetectSettingsForTest.Threshold is > 5 and <= 12 && w.DetectNoteForTest.Contains("上げて"));
            Check("絵の大部分を覆う囲みは外す", w.DetectNoteForTest.Contains("外しました") && found >= 5);
            log.Add($"     自動で探した囲み: {found} 個");
            Check("重ねた絵から囲みが見つかる", found >= 5);
            await Task.Delay(300);
            SaveWindowPng(w, Path.ChangeExtension(reportPath, ".window.png"));
            var spec = w.MakeSpecForTest()!;
            w.Close();

            var panelPath = Path.Combine(workDir, "panel.txt");
            Check("新しいパネルを作れる", ApplyCropBuild(spec, panelPath, interactive: false));
            string Full(string rel) => Path.GetFullPath(Path.Combine(workDir, rel));
            var day = _doc!.MainPanel?.Get("DaytimeImage");
            var night = _doc.MainPanel?.Get("NighttimeImage");
            Check("[This] は画像なし・メインパネルは「パネル本体」の組とつながる", _doc.This?.Get("DaytimeImage") == null && _doc.MainPanel?.CropSet == Core.Crop.CropSetNames.PanelBody);
            log.Add($"     パネル本体: 昼={day} 夜={night}");
            Check("パネル本体の昼は書き出した PNG（印入り）", day != null && day.EndsWith(".png") && PngWriter.IsPanelKunPng(Full(day)));
            Check("パネル本体の夜は書き出した PNG（印入り）", night != null && night.EndsWith(".png") && PngWriter.IsPanelKunPng(Full(night)));
            var text = Editor.Text;
            Check("本体の切り抜き元とレイヤーの行が入る", text.Contains(";===== 切り抜き元 パネル本体: 昼=") && text.Contains(";@切り抜き元レイヤー パネル本体 昼: "));
            Check("発光の切り抜き元とレイヤーの行が入る", text.Contains(";@切り抜き元レイヤー 発光 昼: ") && text.Contains(";@切り抜き元レイヤー 発光 夜: "));
            Check("画像の問題はない", !_scene!.Problems.Any(p => p.Message.Contains("画像")));
            foreach (var line in text.Split('\n').Where(l => l.Contains("切り抜き元")).Take(8)) log.Add("       " + line.TrimEnd('\r'));

            Check("保存できる", Save(_path));
            int count = _doc.Sections.Count;
            LoadPanel(_path!, null);
            Check("開き直してもつながりとレイヤーが残る", _doc!.HasCropLink && _doc.Sections.Count == count
                && _doc.CropSets.Any(c => c.Name == Core.Crop.CropSetNames.PanelBody && c.DayLayers?.Count > 0));
            Check("開き直しても書き出しが同じ", Editor.Text == text);

            var t = Editor.Text;
            var baseBytes = File.ReadAllBytes(Full(day!));
            var lampImg = Full(_doc.Sections.First(s => s.CropSet != null).Get("DaytimeImage")!);
            var lampBytes = File.ReadAllBytes(lampImg);
            var plan = RunCropUpdate(interactive: false);
            log.Add($"     更新: 画像 {plan?.Files.Count} 枚、飛ばした {plan?.Skipped.Count}、切り出せない {plan?.Missing.Count}、注意 {plan?.Warnings.Count}");
            foreach (var x in plan?.Warnings.Take(5) ?? []) log.Add("       注意: " + x);
            Check("切り抜き元（PSD）から更新できる", plan != null && plan.Missing.Count == 0 && plan.Skipped.Count == 0
                && plan.Files.Count == _doc.Sections.Count(s => s.CropSet != null) * 2); // メインパネルも表示灯として（[This] は透明なので作り直さない）
            Check("すぐ更新してもパネル・本体・表示灯は変わらない", Editor.Text == t
                && File.ReadAllBytes(Full(day!)).AsSpan().SequenceEqual(baseBytes) && File.ReadAllBytes(lampImg).AsSpan().SequenceEqual(lampBytes));
            Check("PSD は書き換えていない", File.ReadAllBytes(psdPath).Length == psdBytes);

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
