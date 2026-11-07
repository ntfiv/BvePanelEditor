using System.IO;
using System.Runtime.InteropServices;
using BvePanelEditor.Views;

namespace BvePanelEditor;

/// <summary>
/// パネルファイルを保存する前に書き出した画像（とそのために作ったフォルダ）を覚えておき、保存しないで閉じるときに、ゴミ箱に送るか聞く。
/// すでにあったファイルを上書きしたものは覚えない（元に戻せないので消さない）。保存したら忘れる（パネルが使うので）。
/// </summary>
public partial class MainWindow
{
    private readonly List<string> _pendingFiles = [];
    private readonly List<string> _pendingDirs = [];

    /// <summary>確認用: 設定すると、ゴミ箱に送る代わりにこの関数を呼ぶ（送れたら true）。</summary>
    public static Func<string, bool>? TrashForTest;

    /// <summary>ファイルを書き出す。新しく作ったファイル・フォルダなら覚えておく（保存しないで閉じるときに聞くため）。</summary>
    private void WriteNewFile(string path, byte[] bytes)
    {
        var full = Path.GetFullPath(path);
        bool existed = File.Exists(full);
        EnsureDir(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        if (!existed && !_pendingFiles.Contains(full, StringComparer.OrdinalIgnoreCase)) _pendingFiles.Add(full);
    }

    /// <summary>フォルダを作る。新しく作ったフォルダ（親も）を覚えておく。</summary>
    private void EnsureDir(string dir)
    {
        var made = new List<string>();
        for (var d = Path.GetFullPath(dir); !string.IsNullOrEmpty(d) && !Directory.Exists(d); d = Path.GetDirectoryName(d)!) made.Add(d);
        Directory.CreateDirectory(dir);
        foreach (var d in made) if (!_pendingDirs.Contains(d, StringComparer.OrdinalIgnoreCase)) _pendingDirs.Add(d);
    }

    /// <summary>保存したので、書き出した画像はパネルが使う。覚えていたものを忘れる。</summary>
    private void ForgetPendingFiles()
    {
        _pendingFiles.Clear();
        _pendingDirs.Clear();
    }

    /// <summary>まだ残っている、保存前に書き出した画像。</summary>
    public List<string> PendingFiles => _pendingFiles.Where(File.Exists).ToList();

    /// <summary>
    /// 保存しないで閉じる・別のファイルを開くとき: 保存前に書き出した画像が残っていれば、ゴミ箱に送るか聞く。
    /// 送る・残すなら true、やめるなら false（閉じない）。
    /// </summary>
    private bool AskTrashPendingFiles()
    {
        var files = PendingFiles;
        if (files.Count == 0) { ForgetPendingFiles(); return true; }
        // 確認用の起動では、答えが用意されているときだけ聞く（画面で止まらないように）
        if (IsTestRun && ChoiceDialog.TestAnswer == null) { ForgetPendingFiles(); return true; }
        var folders = files.Select(f => Path.GetDirectoryName(f)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var where = string.Join("\n", folders.Take(4).Select(f => "・" + f)) + (folders.Count > 4 ? $"\n…ほか {folders.Count - 4} か所" : "");
        int pick = ChoiceDialog.Show(this, "書き出した画像",
            $"パネルファイルを保存しないまま閉じます。このパネルのために書き出した画像が {files.Count} 個残っています。\n{where}\n\nゴミ箱に送りますか？（ゴミ箱から元に戻せます）",
            [$"ゴミ箱に送る（{files.Count} 個。空になったフォルダも）", "残す"]);
        if (pick < 0) return false;
        if (pick == 0)
        {
            int failed = 0;
            foreach (var f in files) if (!SendToTrash(f)) failed++;
            // 作ったフォルダは、空になっていれば深いものから送る
            foreach (var d in _pendingDirs.Where(Directory.Exists).OrderByDescending(d => d.Length))
            {
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) SendToTrash(d); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            if (failed > 0) Warn($"{failed} 個の画像をゴミ箱に送れませんでした（使われているかもしれません）。");
        }
        ForgetPendingFiles();
        return true;
    }

    /// <summary>ファイル・フォルダをゴミ箱に送る（元に戻せるように）。</summary>
    private static bool SendToTrash(string path)
    {
        if (TrashForTest != null) return TrashForTest(path);
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted && !File.Exists(path) && !Directory.Exists(path);
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
}
