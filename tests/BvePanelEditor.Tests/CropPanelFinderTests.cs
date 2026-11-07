using BvePanelEditor.Core;
using BvePanelEditor.Core.Crop;
using Xunit;

namespace BvePanelEditor.Tests;

/// <summary>画像から作ったパネルを探す（画像を開いたとき、作り直して上書きしないため）。</summary>
public sealed class CropPanelFinderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "panelkun_finder_" + Guid.NewGuid().ToString("N"));
    public CropPanelFinderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Panel(string path, string baseImage, CropSet? set)
    {
        var doc = PanelDocument.CreateNew(baseImage, 100, 100);
        if (set != null) doc.CropSets.Add(set);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, doc.Serialize());
        return path;
    }

    [Fact]
    public void FindsPanelsUsingTheImage()
    {
        var img = Path.Combine(_dir, "src", "panel_TT.png");
        Directory.CreateDirectory(Path.GetDirectoryName(img)!);
        File.WriteAllBytes(img, []);
        // 画像の親のフォルダに置いたパネル（切り抜き元として使う）
        var a = Panel(Path.Combine(_dir, "panel.txt"), Path.Combine("src", "panel.png"), new CropSet { Day = Path.Combine("src", "panel_TT.png") });
        // 画像のフォルダの下（パネル本体として使う）
        var b = Panel(Path.Combine(_dir, "src", "sub", "b.cfg"), Path.Combine("..", "panel_TT.png"), null);
        // 関係ないパネル
        Panel(Path.Combine(_dir, "other.txt"), "x.png", new CropSet { Day = "y.png" });
        File.WriteAllText(Path.Combine(_dir, "readme.txt"), "パネルではないファイル");

        var found = CropPanelFinder.Find(img, []);
        Assert.Equal(2, found.Count);
        Assert.Contains(a, found);
        Assert.Contains(b, found);
    }

    [Fact]
    public void FindsMainPanelStyle()
    {
        // [This] は画像なし、パネル本体はメインパネルの表示灯
        var img = Path.Combine(_dir, "BVEPanel_N.png");
        var doc = MainPanel.CreateNew("BVEPanel.png", "BVEPanel_N.png", 100, 100);
        var path = Path.Combine(_dir, "panel.txt");
        File.WriteAllText(path, doc.Serialize());
        Assert.Equal([path], CropPanelFinder.Find(img, []));
        Assert.Equal([path], CropPanelFinder.Find(Path.Combine(_dir, "BVEPanel.png"), []));
    }

    [Fact]
    public void RememberedPanelElsewhere()
    {
        var img = Path.Combine(_dir, "images", "p.png");
        Directory.CreateDirectory(Path.GetDirectoryName(img)!);
        var far = Path.Combine(_dir, "a", "b", "c", "panel.txt");
        Panel(far, img, new CropSet { Day = img });   // 絶対パスで指している
        Assert.Empty(CropPanelFinder.Find(img, []));   // 探す場所の外
        Assert.Equal([far], CropPanelFinder.Find(img, [far, Path.Combine(_dir, "gone.txt")]));
    }
}
