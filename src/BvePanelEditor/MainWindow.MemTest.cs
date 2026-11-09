using System.Diagnostics;
using System.IO;
using System.Windows;
using BvePanelEditor.Views;

namespace BvePanelEditor;

/// <summary>
/// 確認用: 読み込みをくり返してもメモリが増え続けないか（--memtest 見本のフォルダ 作業フォルダ 結果.txt）。
/// 見本のフォルダ（815Panel.png などの 4 点の画像と、あれば PSD）は読むだけ。作業フォルダにパネルを作る。
/// </summary>
public partial class MainWindow
{
    public async void RunMemTest(string sampleDir, string workDir, string report)
    {
        var log = new List<string>();
        int fail = 0;
        void Check(string name, bool ok) { log.Add($"{(ok ? "OK  " : "FAIL")} {name}"); if (!ok) fail++; }
        static (long Managed, long Private) Mem()
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            using var p = Process.GetCurrentProcess();
            return (GC.GetTotalMemory(true), p.PrivateMemorySize64);
        }
        static string Mb(long b) => $"{b / 1048576.0:0}MB";
        // くり返しの後半で増え続けていないか（最初の 2 回は準備の分として除き、残りの増え方を見る）
        void Trend(string name, List<(long Managed, long Private)> m)
        {
            log.Add($"     {name}: 管理メモリ " + string.Join(" → ", m.Select(x => Mb(x.Managed))) + "　全体 " + string.Join(" → ", m.Select(x => Mb(x.Private))));
            long grow = m[^1].Managed - m[2].Managed;
            Check($"{name}をくり返しても管理メモリが増え続けない（3 回目から最後まで +{Mb(grow)}）", grow < 40L * 1048576);
        }
        try
        {
            Directory.CreateDirectory(workDir);
            var first = Path.Combine(sampleDir, "815Panel.png");
            var bg = Path.Combine(sampleDir, "815bg.png");

            // 1. 画像からパネルを作る画面を開いて、部品を探して、閉じる
            var m1 = new List<(long, long)> { Mem() };
            for (int k = 0; k < 6; k++)
            {
                var w = new ImagePanelWindow(this, workDir, first, 5);
                w.Show();
                await Task.Delay(1200);
                w.Detect(replace: true);
                if (File.Exists(bg)) w.AddOverlayForTest(bg, below: true, 0, 0);
                w.ShowOverlayTabForTest();
                await Task.Delay(300);
                w.Close();
                await Task.Delay(300);
                m1.Add(Mem());
            }
            Trend("画像からパネルを作る画面の開け閉め", m1);

            // 2. パネルを開いて、画像をドロップして常点灯画像として読み込む
            var panel = Path.Combine(workDir, "panel.txt");
            File.WriteAllText(panel, "BveTs Instrument Panel 2.00\r\n\r\n[This]\r\nResolution = 2048\r\n");
            var m2 = new List<(long, long)> { Mem() };
            ChoiceDialog.TestAnswer = (m, _) => m.Contains("どう読み込みますか") ? 0 : -1;
            for (int k = 0; k < 6; k++)
            {
                LoadPanel(panel, null);
                await Task.Delay(200);
                HandleDrop([first], new Point(400, 400));
                await Task.Delay(300);
                _savedText = Editor.Text; // 保存しないで開き直す（確認を出さない）
                m2.Add(Mem());
            }
            Trend("パネルを開いて画像を読み込む", m2);

            // 3. PSD をドロップして常点灯画像として読み込む（見本の PSD があれば）
            var psd = Directory.Exists(Path.Combine(Path.GetDirectoryName(sampleDir)!, "PSD_to_panel"))
                ? Path.Combine(Path.GetDirectoryName(sampleDir)!, "PSD_to_panel", "kako817_back.psd") : null;
            if (psd != null && File.Exists(psd))
            {
                var psdCopy = Path.Combine(workDir, "back.psd");
                File.Copy(psd, psdCopy, true);
                LayerAssignAnswer = w => { w.Show(); return true; }; // 結果を読んでから閉じる（ふだんは OK のときに絵を作ってから閉じる）
                var m3 = new List<(long, long)> { Mem() };
                for (int k = 0; k < 6; k++)
                {
                    LoadPanel(panel, null);
                    await Task.Delay(200);
                    HandleDrop([psdCopy], new Point(400, 400));
                    await Task.Delay(300);
                    foreach (var lw in Application.Current.Windows.OfType<LayerAssignWindow>().ToList()) lw.Close();
                    _savedText = Editor.Text;
                    m3.Add(Mem());
                }
                LayerAssignAnswer = null;
                Trend("パネルを開いて PSD を読み込む", m3);
            }
            else log.Add("     （見本の PSD がないので、PSD の読み込みは確かめていません）");
        }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); fail++; }
        ChoiceDialog.TestAnswer = null;
        LayerAssignAnswer = null;
        log.Add(fail == 0 ? "ALL PASSED" : $"{fail} FAILED");
        File.WriteAllLines(report, log);
        _savedText = Editor.Text;
        Close();
    }
}
