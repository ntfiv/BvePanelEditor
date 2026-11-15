using BvePanelEditor.Core.Xcf;
using Xunit;
using Xunit.Abstractions;

namespace BvePanelEditor.Tests;

/// <summary>
/// BVE_XCF_DIR を設定したときだけ動く。その下の XCF（本物の GIMP が書いたファイル）を全部読み（読み取りのみ）、
/// 構造と画素が最後まで読めることを確かめる。XCF には統合画像が無いので、PSD のような見比べはできない。
/// 合成ファイルだけでは「書き手と読み手が同じ勘違いをする」事故を拾えないため、この照合が本命。
/// </summary>
public class XcfRealFileTests(ITestOutputHelper output)
{
    [Fact]
    public void RealXcfsReadFully()
    {
        var dir = Environment.GetEnvironmentVariable("BVE_XCF_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        var failures = new List<string>();
        int files = 0;
        foreach (var d in dir.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Directory.Exists(d)) continue;
            foreach (var f in Directory.EnumerateFiles(d, "*.xcf", SearchOption.AllDirectories))
            {
                files++;
                try
                {
                    using var xcf = XcfFile.Open(f);
                    int opens = xcf.Layers.Count(l => l.Kind == BvePanelEditor.Core.Psd.PsdLayerKind.FolderOpen);
                    int ends = xcf.Layers.Count(l => l.Kind == BvePanelEditor.Core.Psd.PsdLayerKind.FolderEnd);
                    if (opens != ends) failures.Add($"{f}: フォルダの開始 {opens} と終了 {ends} が対になっていません");
                    foreach (var l in xcf.PixelLayers)
                    {
                        var px = xcf.GetPixels(l);
                        if (px == null && !l.Bounds.IsEmpty)
                            failures.Add($"{f}: レイヤー「{l.Path}」の画素が読めませんでした");
                    }
                    output.WriteLine($"{Path.GetFileName(f)}: 版 {xcf.Version}、精度 {xcf.Precision}、レイヤー {xcf.Layers.Count}、警告 {xcf.Warnings.Count}");
                }
                catch (Exception ex)
                {
                    failures.Add($"{f}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        Assert.True(files > 0, "BVE_XCF_DIR の下に XCF がありません");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
