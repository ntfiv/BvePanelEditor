using BvePanelEditor.Core;
using BvePanelEditor.Core.Crop;
using BvePanelEditor.Core.Psd;
using Xunit;

namespace BvePanelEditor.Tests;

/// <summary>指針の蓋: 切り抜く形（丸・多角形）と、蓋の丸を見つける。</summary>
public sealed class CoverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "panelkun_cover_" + Guid.NewGuid().ToString("N"));
    public CoverTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static PsdPixels Solid(int w, int h, byte v) => new(new PsdRect(0, 0, w, h), Enumerable.Repeat(v, w * h * 4).ToArray());
    private static byte Alpha(PsdPixels p, int x, int y) => p.Bgra[(y * p.Width + x) * 4 + 3];

    /// <summary>灰色の板に、(cx, cy) 中心・半径 r の白い丸（蓋）と、その外に半径 ring の黒い輪を描く。</summary>
    private static PsdPixels Dial(int w, int h, double cx, double cy, double r, double ring = 0)
    {
        var p = Solid(w, h, 128);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double d = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy));
                byte v = d <= r ? (byte)230 : ring > 0 && Math.Abs(d - ring) <= 1.5 ? (byte)20 : (byte)128;
                int i = (y * w + x) * 4;
                p.Bgra[i] = p.Bgra[i + 1] = p.Bgra[i + 2] = v;
            }
        return p;
    }

    [Fact]
    public void CircleMaskKeepsInsideAndClearsOutside()
    {
        var img = Solid(20, 20, 200);
        var m = CropShape.Mask(img, [CropShape.Circle(10, 10, 8)]);
        Assert.Equal(200, Alpha(m, 10, 10));
        Assert.Equal(0, Alpha(m, 0, 0));
        Assert.Equal(0, Alpha(m, 19, 19));
        // 縁はなめらか（0 と 200 の間）
        Assert.Contains(Enumerable.Range(0, 400), i => m.Bgra[i * 4 + 3] is > 0 and < 200);
        // 色はそのまま
        Assert.Equal(200, m.Bgra[(10 * 20 + 10) * 4]);
    }

    [Fact]
    public void ShapesAreAddedTogether()
    {
        var img = Solid(30, 20, 255);
        var m = CropShape.Mask(img, [CropShape.Circle(5, 10, 4), CropShape.Polygon([(20, 2), (28, 2), (28, 18), (20, 18)])]);
        Assert.Equal(255, Alpha(m, 5, 10));
        Assert.Equal(255, Alpha(m, 24, 10));
        Assert.Equal(0, Alpha(m, 14, 10));
        Assert.Equal(new PsdRect(1, 2, 28, 18), CropShape.Bounds([CropShape.Circle(5, 10, 4), CropShape.Polygon([(20, 2), (28, 2), (28, 18), (20, 18)])]));
    }

    [Fact]
    public void EllipseMaskUsesBothRadii()
    {
        var img = Solid(40, 20, 200);
        var e = CropShape.Ellipse(20, 10, 18, 6);
        var m = CropShape.Mask(img, [e]);
        Assert.Equal(200, Alpha(m, 35, 10)); // 横には遠くまで入る
        Assert.Equal(0, Alpha(m, 20, 1));    // 縦は狭い
        Assert.Equal(new PsdRect(2, 4, 38, 16), e.Bounds());
        var moved = e.Offset(1, 2);
        Assert.True(moved.IsEllipse);
        Assert.Equal((21.0, 12.0, 18.0, 6.0), (moved.Cx, moved.Cy, moved.R, moved.Ry));
        Assert.True(e.Clone().IsEllipse);
        Assert.Equal(5, CropShape.Circle(0, 0, 5).Ry);
    }

    [Fact]
    public void NoShapesMeansRectangle()
    {
        var img = Solid(4, 4, 9);
        Assert.Same(img, CropShape.Mask(img, []));
    }

    [Theory]
    [InlineData("丸 32,32 r=28")]
    [InlineData("丸 10.5,7 r=3.25")]
    [InlineData("楕円 32,30 rx=28 ry=12.5")]
    [InlineData("多角形 0,10 40,10 40,60 0,60")]
    public void TextRoundTrips(string text)
    {
        Assert.True(CropShape.TryParse(text, out var s));
        Assert.Equal(text, s.ToText());
    }

    [Theory]
    [InlineData("丸 32 r=28")]
    [InlineData("丸 1,2 r=0")]
    [InlineData("多角形 0,0 1,1")]
    [InlineData("四角 0,0")]
    [InlineData("楕円 1,1 rx=3")]
    [InlineData("楕円 1,1 rx=3 ry=0")]
    public void BadTextIsRejected(string text) => Assert.False(CropShape.TryParse(text, out _));

    [Fact]
    public void ShapeLinesRoundTripInPanelFile()
    {
        var text = "BveTs Instrument Panel 2.00\r\n;===== 切り抜き元 元 Panel: 昼=Panel.png | 夜=PanelN.png =====\r\n\r\n"
            + "[PilotLamp]\r\n;@切り抜き: 元 Panel\r\n;@切り抜き形: 丸 25,25 r=24.5\r\n;@切り抜き形: 多角形 0,30 50,30 50,50\r\nSubject = true\r\nLocation = 468, 1649\r\n";
        var doc = PanelDocument.Parse(text);
        Assert.Empty(doc.GroupWarnings);
        var s = doc.Sections[0];
        Assert.Equal(2, s.CropShapes.Count);
        Assert.True(s.CropShapes[0].IsCircle);
        Assert.Equal(24.5, s.CropShapes[0].R);
        Assert.Equal(3, s.CropShapes[1].Points.Count);
        Assert.Equal(text, doc.Serialize());
        Assert.Equal(2, s.Clone().CropShapes.Count);
        // 本文の行には入らない
        Assert.DoesNotContain(s.Lines, l => l.Raw.Contains("切り抜き形"));
    }

    [Fact]
    public void DuplicateDropsShapes()
    {
        var doc = PanelDocument.Parse("BveTs Instrument Panel 2.00\r\n;===== グループ: g =====\r\n[PilotLamp]\r\n;@切り抜き: a\r\n;@切り抜き形: 丸 2,2 r=2\r\nLocation = 0, 0\r\n;===== グループ終わり: g =====\r\n");
        var copies = doc.DuplicateGroup("g", "g2", 1, 1).Indices;
        Assert.Empty(doc.Sections[copies[0]].CropShapes);
    }

    [Fact]
    public void FindsCoverCircle()
    {
        var img = Dial(200, 200, 100.5, 98.5, 25, ring: 60);
        var r = CoverCircle.Detect(img, 100, 100, 45)!;
        Assert.InRange(r.Cx, 99.5, 101.5);
        Assert.InRange(r.Cy, 97.5, 99.5);
        Assert.InRange(r.R, 24, 26);
    }

    [Fact]
    public void ListsOtherRingsAsCandidates()
    {
        var img = Dial(200, 200, 100, 100, 12, ring: 40);
        var all = CoverCircle.DetectAll(img, 100, 100, 60);
        Assert.Contains(all, c => Math.Abs(c.R - 12) <= 1.5);
        Assert.Contains(all, c => Math.Abs(c.R - 40) <= 2.5);
    }

    /// <summary>灰色の板（unlit）と、全体を d だけ明るくして灯を 3 つ点けた板（lit）。big なら左半分以上を強く変える。</summary>
    private static (PsdPixels Unlit, PsdPixels Lit) LampBoard(byte d, bool big)
    {
        var u = Solid(200, 100, 100);
        var l = Solid(200, 100, (byte)(100 + d));
        void Fill(int x0, int y0, int x1, int y1)
        {
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { int i = (y * 200 + x) * 4; l.Bgra[i] = l.Bgra[i + 1] = l.Bgra[i + 2] = 250; }
        }
        Fill(150, 10, 160, 20); Fill(170, 10, 180, 20); Fill(150, 60, 160, 70);
        if (big) Fill(0, 0, 120, 100);
        return (u, l);
    }

    [Fact]
    public void DetectAutoRaisesThresholdWhenEverythingMerges()
    {
        // 全体が少し（8）明るい: 感度 5 だと 1 つにまとまるので、感度を上げて灯 3 つを見つける
        var (u, l) = LampBoard(8, big: false);
        var (rects, t, removed) = Core.Crop.RegionDetect.DetectAuto(u, l, 5, 2);
        Assert.True(t > 5);
        Assert.Equal(3, rects.Count);
        Assert.Equal(0, removed);
    }

    [Fact]
    public void DetectAutoKeepsThresholdAndDropsHugeRegion()
    {
        // 灯はちゃんと分かれていて、広く変わった所（絵の 6 割）だけがある: 感度は上げず、その囲みだけ外す
        var (u, l) = LampBoard(0, big: true);
        var (rects, t, removed) = Core.Crop.RegionDetect.DetectAuto(u, l, 5, 2);
        Assert.Equal(5, t);
        Assert.Equal(1, removed);
        Assert.Equal(3, rects.Count);
    }

    [Fact]
    public void DetectAutoLeavesNormalImagesAlone()
    {
        var (u, l) = LampBoard(0, big: false);
        var (rects, t, removed) = Core.Crop.RegionDetect.DetectAuto(u, l, 5, 2);
        Assert.Equal((5, 0, 3), (t, removed, rects.Count));
    }

    [Fact]
    public void FlatImageHasNoCandidates() => Assert.Empty(CoverCircle.DetectAll(Solid(100, 100, 128), 50, 50, 40));

    [Fact]
    public void UpdateKeepsTheShape()
    {
        // 元の画像（パネルの左上に置く）から、丸で切り抜いた蓋
        var src = Dial(100, 80, 50, 40, 10);
        var panel = Path.Combine(_dir, "panel.png");
        File.WriteAllBytes(panel, PngWriter.Encode(src));
        var shapes = new List<CropShape> { CropShape.Circle(10, 10, 10) };
        var cut = CropShape.Mask(RegionDetect.CropOf(src, new PsdRect(40, 30, 60, 50)), shapes);
        Directory.CreateDirectory(Path.Combine(_dir, "crop"));
        File.WriteAllBytes(Path.Combine(_dir, "crop", "蓋.png"), PngWriter.EncodePow2(cut));

        var doc = PanelDocument.CreateNew("panel.png", 100, 80);
        doc.CropSets.Add(new CropSet { Name = "元 panel", Day = "panel.png" });
        var sec = PanelSection.Create(SectionKind.PilotLamp);
        sec.Set("Subject", "true");
        sec.Set("Location", "40, 30");
        sec.Set("DaytimeImage", Path.Combine("crop", "蓋.png"));
        sec.CropSet = "元 panel";
        sec.CropShapes.AddRange(shapes);
        doc.Sections.Add(sec);

        var plan = CropUpdate.Plan(doc, _dir, p => p == panel ? src : null);
        Assert.Empty(plan.Missing);
        var f = Assert.Single(plan.Files);
        Assert.Equal(File.ReadAllBytes(f.FullPath), f.Png);
        var png = TestPngReader.Read(f.Png);
        Assert.Equal(0, png.Rgba[3]);                        // 角は透明
        Assert.Equal(128, png.Rgba[(10 * png.W + 10) * 4 + 3]); // 真ん中は元のまま（板の A は 128）
    }
}
