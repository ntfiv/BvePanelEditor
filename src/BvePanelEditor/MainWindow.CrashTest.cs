using System.IO;

namespace BvePanelEditor;

/// <summary>確認用: 予期しないエラーのときのログと、未保存の内容の控え（--crashtest 結果.txt）。メッセージは出さない。</summary>
public partial class MainWindow
{
    public async void RunCrashTest(string report)
    {
        var log = new List<string>();
        int fail = 0;
        void Check(string name, bool ok) { log.Add($"{(ok ? "OK  " : "FAIL")} {name}"); if (!ok) fail++; }
        try
        {
            Exception ex;
            try { throw new InvalidOperationException("確認用のエラー\n2 行目"); } catch (InvalidOperationException e) { ex = e; }
            var path = CrashReport.WriteLog(ex);
            log.Add($"     ログ: {path}");
            var text = path != null ? File.ReadAllText(path) : "";
            Check("ログに版・日時・スタックトレースを書く", path != null && path.StartsWith(CrashReport.LogDir) && text.Contains(Views.AboutWindow.DisplayVersion)
                && text.Contains("確認用のエラー") && text.Contains("RunCrashTest"));
            if (path != null) File.Delete(path);
            Check("何も開いていなければ控えはない", RecoveryContent() == null);
            var tmp = Path.Combine(Path.GetTempPath(), "panelkun_crashtest.txt");
            File.WriteAllText(tmp, "BveTs Instrument Panel 2.00\r\n\r\n[This]\r\nResolution = 100\r\n");
            LoadPanel(tmp, null);
            await Task.Delay(300);
            Check("開いたばかり（保存済み）なら控えはない", RecoveryContent() == null);
            Editor.Document.Insert(Editor.Document.TextLength, "\r\n; 未保存\r\n");
            Check("未保存の内容があれば控えに使う", RecoveryContent() is { } r && r.Text.Contains("; 未保存") && r.Name == "panelkun_crashtest.txt");
            // バージョン情報の画面
            var about = new Views.AboutWindow(this);
            about.Show();
            await Task.Delay(500);
            SaveWindowPng(about, Path.ChangeExtension(report, ".about.png"));
            Check("バージョン情報は 0.3.1α", Views.AboutWindow.DisplayVersion == "0.3.1α" && File.Exists(Path.Combine(AppContext.BaseDirectory, "LICENSE.txt"))
                && File.Exists(Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt")));
            about.Close();
        }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); fail++; }
        log.Add(fail == 0 ? "ALL PASSED" : $"{fail} FAILED");
        File.WriteAllLines(report, log);
        _savedText = Editor.Text;
        Close();
    }
}
