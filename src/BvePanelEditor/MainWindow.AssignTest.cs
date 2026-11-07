using System.IO;
using BvePanelEditor.Views;

namespace BvePanelEditor;

/// <summary>確認用: 重ねる画像の PSD を昼・夜に振り分ける画面（--assigntest PSD 画面.png 結果.txt。PSD は読むだけ）。</summary>
public partial class MainWindow
{
    public async void RunAssignTest(string psd, string png, string report)
    {
        var log = new List<string>();
        int fail = 0;
        void Check(string name, bool ok) { log.Add($"{(ok ? "OK  " : "FAIL")} {name}"); if (!ok) fail++; }
        try
        {
            // 「重ねる画像」で PSD を足すときと同じ開き方
            var w = new LayerAssignWindow(this, [psd], ["昼の画像", "夜の画像"], [new(false, false), new(true, false)], [psd, psd], [null, null],
                $"重ねる画像「{Path.GetFileNameWithoutExtension(psd)}」");
            w.Show();
            await Task.Delay(800);
            var c = w.CheckedForTest;
            log.Add("     昼: " + string.Join("、", c[0]));
            log.Add("     夜: " + string.Join("、", c[1]));
            Check("題名に何の画像か出す", w.Title.Contains("重ねる画像「"));
            Check("昼は night の付かないレイヤー（対のあるものも含む）", c[0].Count > 0 && c[0].All(l => !l.Contains("night", StringComparison.OrdinalIgnoreCase)));
            Check("夜は night のレイヤーと、対のないレイヤー（対のある昼のレイヤーは入れない）", c[1].Count > 0
                && !c[1].Any(l => !l.Contains("night", StringComparison.OrdinalIgnoreCase) && c[1].Any(n => n.Replace("_Night", "", StringComparison.OrdinalIgnoreCase) == l)));
            SaveWindowPng(w, png);
            w.ClearChecks();
            await Task.Delay(300);
            Check("チェックをすべて外す", w.CheckedForTest.All(x => x.Count == 0));
            SaveWindowPng(w, Path.ChangeExtension(png, ".clear.png"));
            w.Close();
        }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); fail++; }
        log.Add(fail == 0 ? "ALL PASSED" : $"{fail} FAILED");
        File.WriteAllLines(report, log);
        Close();
    }
}
