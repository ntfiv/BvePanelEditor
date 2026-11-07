namespace BvePanelEditor;

/// <summary>パネルファイルを開く・保存する画面の絞り込み。BVE のパネルファイルは .txt のほか .cfg も使われる。</summary>
public static class PanelFilters
{
    /// <summary>開く: パネル・車両・シナリオ（.txt と .cfg）。</summary>
    public const string Open = "BVE のファイル（*.txt, *.cfg）|*.txt;*.cfg|画像・PSD（パネルを作る）|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.psd|すべて|*.*";

    /// <summary>保存: 1 番目は両方、2 番目 .txt、3 番目 .cfg。</summary>
    public const string Save = "パネルファイル（*.txt, *.cfg）|*.txt;*.cfg|テキスト（*.txt）|*.txt|設定ファイル（*.cfg）|*.cfg";

    /// <summary>今のファイルの拡張子に合わせた保存の絞り込みの番号（1 始まり）。</summary>
    public static int IndexFor(string? path) =>
        path != null && System.IO.Path.GetExtension(path).Equals(".cfg", StringComparison.OrdinalIgnoreCase) ? 3 : 1;
}
