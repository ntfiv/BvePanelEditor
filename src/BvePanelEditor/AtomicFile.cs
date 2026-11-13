using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BvePanelEditor;

/// <summary>ファイルを一時ファイルに書いてから差し替える。書き込みの途中で失敗しても、元のファイルは壊れない。</summary>
internal static class AtomicFile
{
    public static void WriteAllBytes(string path, byte[] bytes)
    {
        string full = Path.GetFullPath(path);
        string dir = Path.GetDirectoryName(full)!;
        string tmp = Path.Combine(dir, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(full)) File.Replace(tmp, full, null);
            else File.Move(tmp, full);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
        }
    }

    /// <summary>File.WriteAllLines と同じ中身（文字コードの BOM と、行ごとの改行）を、一時ファイル経由で書く。</summary>
    public static void WriteAllLines(string path, IEnumerable<string> lines, Encoding encoding)
    {
        var sb = new StringBuilder();
        foreach (var l in lines) sb.Append(l).Append(Environment.NewLine);
        var pre = encoding.GetPreamble();
        var body = encoding.GetBytes(sb.ToString());
        var bytes = new byte[pre.Length + body.Length];
        pre.CopyTo(bytes, 0);
        body.CopyTo(bytes, pre.Length);
        WriteAllBytes(path, bytes);
    }
}
