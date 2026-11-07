using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using BvePanelEditor.Views;

namespace BvePanelEditor;

/// <summary>
/// 予期しないエラーのとき: 詳しい内容（スタックトレースなど）はログファイルに書き、未保存の編集は控えを取り、
/// 画面には短いメッセージだけを出す。ログ・控えは %AppData%\BvePanelEditor の logs・recovery フォルダ。
/// </summary>
public static class CrashReport
{
    private static string BaseDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BvePanelEditor");
    public static string LogDir => Path.Combine(BaseDir, "logs");
    public static string RecoveryDir => Path.Combine(BaseDir, "recovery");

    private static bool _showing;
    private static DateTime _lastShown;

    /// <summary>ログファイルに書く（書けなければ null）。</summary>
    public static string? WriteLog(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            var path = Path.Combine(LogDir, $"error_{DateTime.Now:yyyyMMdd_HHmmss_fff}.txt");
            var sb = new StringBuilder();
            sb.AppendLine($"{MainWindow.AppName} {AboutWindow.DisplayVersion}");
            sb.AppendLine($"日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Windows: {Environment.OSVersion}　.NET: {Environment.Version}");
            sb.AppendLine();
            sb.AppendLine(ex.ToString());
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>未保存の内容の控えを書く（なければ・書けなければ null）。</summary>
    private static string? WriteRecovery(MainWindow? main)
    {
        try
        {
            if (main?.RecoveryContent() is not { } r) return null;
            Directory.CreateDirectory(RecoveryDir);
            var name = Path.GetFileNameWithoutExtension(r.Name);
            var ext = Path.GetExtension(r.Name) is { Length: > 0 } x ? x : ".txt";
            var path = Path.Combine(RecoveryDir, $"{name}_控え_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
            File.WriteAllText(path, r.Text, new UTF8Encoding(true));
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }

    /// <summary>ログを書き、控えを取り、短いメッセージを出す。続けて何度も起きたときは、ログだけ書いて出さない。</summary>
    public static void Show(Exception ex, MainWindow? main)
    {
        var log = WriteLog(ex);
        if (_showing || (DateTime.Now - _lastShown).TotalSeconds < 2) return;
        _showing = true;
        try
        {
            var recovery = WriteRecovery(main);
            var first = ex.Message.Split('\n')[0].Trim();
            var text = "予期しないエラーが起きました。\n\n" + first + "\n\n"
                + "そのまま作業を続けられることがありますが、念のため保存してから、パネルくんを起動し直してください。\n\n"
                + (recovery != null ? $"まだ保存していない内容の控え:\n{recovery}\n\n" : "")
                + (log != null ? $"詳しい内容（不具合を知らせるときに添えてください）:\n{log}\n\n" : "")
                + "ログのフォルダを開きますか？";
            var owner = main is { IsLoaded: true } ? main : null;
            var answer = owner != null
                ? MessageBox.Show(owner, text, "予期しないエラー", MessageBoxButton.YesNo, MessageBoxImage.Error, MessageBoxResult.No)
                : MessageBox.Show(text, "予期しないエラー", MessageBoxButton.YesNo, MessageBoxImage.Error, MessageBoxResult.No);
            if (answer == MessageBoxResult.Yes && log != null)
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{log}\"") { UseShellExecute = true }); }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        }
        finally
        {
            _showing = false;
            _lastShown = DateTime.Now;
        }
    }
}
