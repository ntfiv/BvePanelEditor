using System.IO;
using BvePanelEditor.Core;
using BvePanelEditor.Core.Psd;

namespace BvePanelEditor;

/// <summary>
/// 確認用: コメントアウトして無効にしたオブジェクト（--disabledtest 作業フォルダ 画面.png 結果.txt）。
/// 作業フォルダに、同梱の E217 の針で「有効な針」と「コメントアウトした針」のパネルを作って開く。
/// </summary>
public partial class MainWindow
{
    public async void RunDisabledTest(string workDir, string png, string report)
    {
        var log = new List<string>();
        int fail = 0;
        void Check(string name, bool ok) { log.Add($"{(ok ? "OK  " : "FAIL")} {name}"); if (!ok) fail++; }
        try
        {
            Directory.CreateDirectory(workDir);
            File.Copy(Path.Combine(NeedleCatalog.E217Folder, "needle_speed_E217.png"), Path.Combine(workDir, "needle_speed.png"), true);
            var gray = new byte[512 * 512 * 4];
            for (int i = 0; i < gray.Length; i += 4) { gray[i] = gray[i + 1] = gray[i + 2] = 90; gray[i + 3] = 255; }
            File.WriteAllBytes(Path.Combine(workDir, "base.png"), PngWriter.Encode(512, 512, gray));
            var text = "BveTs Instrument Panel 2.00\r\n\r\n[This]\r\nResolution = 512\r\nDaytimeImage = base.png\r\n\r\n"
                + "[Needle] ; 速度計\r\nSubject = kmph\r\nLocation = 150, 300\r\nDaytimeImage = needle_speed.png\r\nOrigin = 8, 256\r\nRadius = 180\r\nInitialAngle = -120\r\nLastAngle = 120\r\nMaximum = 160\r\n\r\n"
                + ";[Needle] ; 調整用の針\r\n;Subject = kmph\r\n;Location = 380, 300\r\n;DaytimeImage = needle_speed.png\r\n;Origin = 8, 256\r\n;Radius = 180\r\n;InitialAngle = -120\r\n;LastAngle = 120\r\n;Maximum = 160\r\n";
            var panel = Path.Combine(workDir, "panel.txt");
            File.WriteAllText(panel, text);
            LoadPanel(panel, null);
            _states.SetValue("kmph", 80);
            Canvas.FitAll();
            UpdateLayout();
            await Task.Delay(800);

            Check("コメントアウトした針も一覧に出る（3 つ）", _doc!.Sections.Count == 3 && _doc.Sections[2].Disabled && _doc.Sections[2].Title == "調整用の針");
            Check("コメントアウトした針はパネル画面に描かない", _scene!.ItemFor(2) == null && _scene.ItemFor(1) != null);
            var rows = (ElementList.ItemsSource as IEnumerable<ElementRow>)!.ToList();
            Check("一覧ではコメントアウトしたオブジェクトの背景が灰色", rows.First(r => r.Index == 2).Disabled && rows.First(r => r.Index == 1).Disabled == false
                && rows.First(r => r.Index == 2).RowBackground != System.Windows.Media.Brushes.Transparent);

            // 有効な針をコメントアウトする
            ToggleDisabled([1]);
            await Task.Delay(300);
            Check("コメントアウトすると、テキストの行の頭に「;」が付く", Editor.Text.Contains(";[Needle] ; 速度計\r\n;Subject = kmph\r\n;Location = 150, 300"));
            Check("コメントアウトした針は描かない", _scene!.ItemFor(1) == null && _doc!.Sections[1].Disabled);
            // 戻す
            ToggleDisabled([1]);
            await Task.Delay(300);
            Check("もう一度でコメントを外して元のテキストに戻る", Editor.Text == text && _scene!.ItemFor(1) != null);
            // 調整用の針を有効にする → 元に戻す（Ctrl+Z）
            ToggleDisabled([2]);
            await Task.Delay(300);
            Check("調整用の針のコメントを外すと描く", _scene!.ItemFor(2) != null && Editor.Text.Contains("\r\n[Needle] ; 調整用の針\r\nSubject = kmph"));
            Editor.Undo();
            FlushReparse();
            await Task.Delay(300);
            Check("Ctrl+Z でコメントアウトに戻る", Editor.Text == text && _doc!.Sections[2].Disabled);
            // コメントアウトしたままでも、値を変えられる（行の頭の「;」は付いたまま）
            SelectSection(2);
            _doc!.Sections[2].Set("Location", "390, 300");
            CommitModel();
            await Task.Delay(300);
            Check("コメントアウトしたオブジェクトの値を変えても「;」は付いたまま", Editor.Text.Contains(";Location = 390, 300") && !Editor.Text.Contains("\nLocation = 390"));
            UpdateLayout();
            await Task.Delay(400);
            SaveWindowPng(this, png);
        }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); fail++; }
        log.Add(fail == 0 ? "ALL PASSED" : $"{fail} FAILED");
        File.WriteAllLines(report, log);
        _savedText = Editor.Text;
        Close();
    }
}
