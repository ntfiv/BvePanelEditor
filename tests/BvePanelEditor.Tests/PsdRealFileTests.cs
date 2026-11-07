using BvePanelEditor.Core.Psd;
using Xunit;
using Xunit.Abstractions;

namespace BvePanelEditor.Tests;

/// <summary>
/// BVE_PSD_DIR を設定したときだけ動く。その下の PSD を全部読み（読み取りのみ）、
/// 表示中のレイヤーを重ねた絵と統合画像がほぼ一致するかを確かめる。
/// </summary>
public class PsdRealFileTests(ITestOutputHelper output)
{
    [Fact]
    public void RealPsdsReadAndCompose()
    {
        var dir = Environment.GetEnvironmentVariable("BVE_PSD_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        var failures = new List<string>();
        foreach (var d in dir.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Directory.Exists(d)) continue;
            foreach (var f in Directory.EnumerateFiles(d, "*.psd", SearchOption.AllDirectories))
            {
                try
                {
                    using var psd = PsdFile.Open(f);
                    int pixel = psd.PixelLayers.Count();
                    var merged = psd.ReadMerged();
                    string compare = "";
                    var visible = PsdCompose.VisibleLayers(psd).ToList();
                    // フォルダを 1 枚にまとめてから合成する描画モード（通過以外）は再現しないので見比べない
                    bool simple = visible.All(l => PsdCompose.IsSupportedBlend(l.BlendMode) && AncestorsPassThrough(l));
                    // 統合画像を作らずに保存した PSD は、統合画像がほぼ単色になっているので見比べない
                    var colors = new HashSet<int>();
                    for (int i = 0; i < merged.Bgra.Length && colors.Count < 8; i += 4 * 97) colors.Add(BitConverter.ToInt32(merged.Bgra, i));
                    if (colors.Count < 4) compare = " 統合画像なし（見比べない）";
                    else if (visible.Count(l => l.HasEffects) is var fx and > 0 && visible.Where(l => l.HasEffects).Sum(l => (long)l.Bounds.Width * l.Bounds.Height) > (long)psd.Width * psd.Height / 100)
                        compare = $" レイヤー効果付きのレイヤー {fx} 個（効果は書き出せないので見比べない）";
                    else if (pixel > 0 && simple && (long)psd.Width * psd.Height <= 4096L * 4096)
                    {
                        var comp = PsdCompose.Compose(psd, visible, psd.Canvas);
                        double sum = 0;
                        long n = 0;
                        for (int i = 0; i < comp.Bgra.Length; i += 4)
                        {
                            if (merged.Bgra[i + 3] < 250) continue; // 透明な所は統合画像の作り方で色が違う
                            // 統合画像は白背景と合成済みのことがあるので、同じく白と合成して比べる
                            float a = comp.Bgra[i + 3] / 255f;
                            for (int c = 0; c < 3; c++)
                            {
                                double v = comp.Bgra[i + c] * a + 255 * (1 - a);
                                sum += Math.Abs(v - merged.Bgra[i + c]);
                                n++;
                            }
                        }
                        double mean = n == 0 ? 0 : sum / n;
                        compare = $" 統合画像との差 平均 {mean:0.00}";
                        // 文字のふちは Photoshop の「テキストカラーをガンマでブレンド」の設定で明るさが変わるので、文字が多い PSD は目安を緩める
                        long textArea = visible.Where(l => l.Kind == PsdLayerKind.Text).Sum(l => (long)l.Bounds.Width * l.Bounds.Height);
                        bool textHeavy = textArea * 5 > (long)psd.Width * psd.Height;
                        if (textHeavy) compare += "（文字が多いので目安を緩める）";
                        if (mean > (textHeavy ? 10 : 3)) failures.Add($"{f}: 差が大きい（{mean:0.00}）");
                    }
                    output.WriteLine($"{Path.GetFileName(f)}: {psd.Width}x{psd.Height} {psd.Depth}bit レイヤー {psd.Layers.Count}（画像 {pixel}、調整 {visible.Count(l => l.Kind == PsdLayerKind.Adjustment)}）{compare}");
                }
                catch (NotSupportedException e)
                {
                    output.WriteLine($"{Path.GetFileName(f)}: 対象外 {e.Message}");
                }
                catch (Exception e)
                {
                    failures.Add($"{f}: {e.GetType().Name} {e.Message}");
                }
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static bool AncestorsPassThrough(PsdLayer l)
    {
        for (var p = l.Parent; p != null; p = p.Parent) if (!PsdCompose.IsNormalBlend(p.BlendMode)) return false;
        return true;
    }
}
