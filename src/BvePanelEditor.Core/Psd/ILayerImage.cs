using System.IO;
using BvePanelEditor.Core.Xcf;

namespace BvePanelEditor.Core.Psd;

/// <summary>
/// レイヤー画像ファイル（PSD・XCF）の共通の読み口。合成（<see cref="PsdCompose"/>）と
/// 読み込み（<see cref="PsdImport"/>）は、この形だけを使って両形式を扱う。
/// レイヤーのモデルはどちらの形式でも <see cref="PsdLayer"/> を使う。
/// </summary>
public interface ILayerImage : IDisposable
{
    string FilePath { get; }
    int Width { get; }
    int Height { get; }
    PsdRect Canvas { get; }
    List<PsdLayer> Layers { get; }
    IEnumerable<PsdLayer> PixelLayers { get; }
    IEnumerable<PsdLayer> ComposableLayers { get; }

    /// <summary>
    /// ファイル全体への注意（△近似・×再現しない）。読み込みの警告に含める。
    /// PSD はファイル単位の注意を持たないので空。
    /// </summary>
    IReadOnlyList<PsdWarning> FileWarnings { get; }

    /// <summary>
    /// 合成をリニア（ガンマなし）の明るさで行うか。GIMP 2.10 世代は、半透明の重なりや
    /// 今の方式の描画モードをリニアで計算するので、XCF はこちらに合わせる。PSD（Photoshop）は sRGB のまま。
    /// </summary>
    bool LinearComposite { get; }

    /// <summary>
    /// 画素のバイト値がリニアの明るさで格納されているか（XCF の 8bit リニア精度=100）。
    /// 通常のファイルは sRGB で格納されているので false。
    /// </summary>
    bool LinearPixels { get; }

    PsdLayer? Find(string path);
    List<PsdLayer> FindAllSameName(string path);
    PsdPixels? GetPixels(PsdLayer layer);
    PsdMask? GetMask(PsdLayer layer);
}

/// <summary>拡張子で PSD／XCF を見分けて開く。</summary>
public static class LayerImage
{
    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".psd", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".xcf", StringComparison.OrdinalIgnoreCase);
    }

    public static ILayerImage Open(string path) =>
        Path.GetExtension(path).Equals(".xcf", StringComparison.OrdinalIgnoreCase)
            ? XcfFile.Open(path)
            : PsdFile.Open(path);
}
