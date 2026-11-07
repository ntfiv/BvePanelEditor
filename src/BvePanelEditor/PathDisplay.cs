using System.IO;

namespace BvePanelEditor;

/// <summary>
/// テスト用の裏機能（Ctrl+9）: 画面に出すファイルのパスをファイル名だけにする（画面を撮るとき、フォルダの名前を出さないため）。
/// 表示だけを変え、ファイルの中身は変えない。覚えておかない（起動するたびにオフ）。
/// </summary>
public static class PathDisplay
{
    public static bool NamesOnly { get; set; }

    /// <summary>パス（フルパス・相対パス）を、今の設定で画面に出す形にする。</summary>
    public static string Show(string? path)
    {
        if (string.IsNullOrEmpty(path)) return path ?? "";
        if (!NamesOnly) return path;
        var p = path.Trim().Trim('"');
        var name = Path.GetFileName(p);
        return name.Length > 0 ? name : p;
    }
}
