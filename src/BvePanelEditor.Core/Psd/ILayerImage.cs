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
