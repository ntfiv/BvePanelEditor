using System.Text;
using BvePanelEditor.Core;
using BvePanelEditor.Core.Imaging;
using BvePanelEditor.Core.Psd;
using Xunit;

namespace BvePanelEditor.Tests;

/// <summary>モニタくんに合わせた読み込み: 4 隅の変形、パネルファイルの一部の取り込み、SVG の外の参照の除去。</summary>
public sealed class MonitorPartsTests
{
    private static PsdPixels Solid(int w, int h, byte b, byte g, byte r, byte a = 255)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = a; }
        return new PsdPixels(new PsdRect(0, 0, w, h), px);
    }

    private static byte Alpha(PsdPixels p, int x, int y) => p.Bgra[(y * p.Width + x) * 4 + 3];

    // ---- 射影変換 ----

    [Fact]
    public void HomographyMapsCornersToQuad()
    {
        var h = Homography.FromRectToQuad(100, 50, (10, 20), (120, 30), (110, 90), (5, 70));
        AssertNear((10, 20), h.Map(0, 0));
        AssertNear((120, 30), h.Map(100, 0));
        AssertNear((110, 90), h.Map(100, 50));
        AssertNear((5, 70), h.Map(0, 50));
        var inv = h.Inverse();
        AssertNear((40, 25), inv.Map(h.Map(40, 25).X, h.Map(40, 25).Y));
    }

    [Fact]
    public void HomographyRejectsDegenerateQuad()
    {
        Assert.Throws<InvalidOperationException>(() => Homography.FromRectToQuad(10, 10, (0, 0), (5, 5), (10, 10), (20, 20)));
    }

    [Fact]
    public void WarpRejectsCrossedConcaveAndHugeQuads()
    {
        var src = Solid(10, 10, 0, 0, 255);
        // 右上と右下を入れ替えた（辺が交差する）形
        Assert.Throws<InvalidOperationException>(() => Warp.ToQuad(src, (0, 0), (100, 100), (100, 0), (0, 100)));
        // へこんだ形（右下の隅が内側へ入る）
        Assert.Throws<InvalidOperationException>(() => Warp.ToQuad(src, (0, 0), (100, 0), (30, 30), (0, 100)));
        // 3 つの隅が一直線
        Assert.Throws<InvalidOperationException>(() => Warp.ToQuad(src, (0, 0), (50, 0), (100, 0), (0, 100)));
        // 囲む範囲が大きすぎる（1 辺 8192 画素まで）
        Assert.Throws<InvalidOperationException>(() => Warp.ToQuad(src, (0, 0), (9000, 0), (9000, 10), (0, 10)));
        Assert.Throws<InvalidOperationException>(() => Warp.ToQuad(src, (0, 0), (double.NaN, 0), (10, 10), (0, 10)));
        // 囲む範囲は小さくても、座標そのものが大きすぎる（整数にするとあふれる）
        Assert.Throws<InvalidOperationException>(() => Warp.ToQuad(src, (3e9, 0), (3e9 + 10, 0), (3e9 + 10, 10), (3e9, 10)));
        Assert.Throws<InvalidOperationException>(() => Warp.ToQuad(src, (-200000, 0), (-199990, 0), (-199990, 10), (-200000, 10)));
        // 上限の内側なら、離れた所でも置ける
        var far = Warp.ToQuad(src, (-99990, 99990), (-99980, 99990), (-99980, 100000), (-99990, 100000));
        Assert.Equal(new PsdRect(-99990, 99990, -99980, 100000), far.Bounds);
        // DigitalNumber も同じ
        Assert.Throws<InvalidOperationException>(() => Warp.FramesToQuad(Solid(10, 20, 0, 0, 0), 10, (0, 0), (100, 100), (100, 0), (0, 100)));
        // 裏返し（反時計回りの並び）は変形できる
        var mirrored = Warp.ToQuad(src, (10, 0), (0, 0), (0, 10), (10, 10));
        Assert.Equal(new PsdRect(0, 0, 10, 10), mirrored.Bounds);
        Assert.Equal(255, Alpha(mirrored, 5, 5));
    }

    [Fact]
    public void ExtremePerspectiveStaysInsideQuad()
    {
        // 上の辺が 2 画素しかない強い遠近（凸の形なら、写した先は 4 隅を囲む四角の中に収まり、数にならない値も出ない）
        var r = Warp.ToQuad(Solid(200, 200, 0, 255, 0), (99, 0), (101, 0), (200, 300), (0, 300));
        Assert.Equal(new PsdRect(0, 0, 200, 300), r.Bounds);
        Assert.Equal(0, Alpha(r, 5, 5));
        // 手前（下）は大きく引き伸ばされるので、元の絵の縁の半画素ぶんが下の数画素にかかる。縁から離れた所で見る
        Assert.Equal(255, Alpha(r, 100, 200));
        Assert.True(Alpha(r, 100, 2) > 0);
        Assert.Equal(255, r.Bgra[(200 * 200 + 100) * 4 + 1]);
    }

    [Fact]
    public void DraftWarpUsesOneSamplePerPixel()
    {
        // 7 分の 1 に縮める変形: 細かい（ふつう）は白黒の縞が混ざって灰色、下書き（1 点）は縞のどちらか 1 本の色
        var src = Solid(63, 63, 0, 0, 0);
        for (int y = 0; y < 63; y++)
            for (int x = 0; x < 63; x += 2) { int o = (y * 63 + x) * 4; src.Bgra[o] = src.Bgra[o + 1] = src.Bgra[o + 2] = 255; }
        var fine = Warp.ToQuad(src, (0, 0), (9, 0), (9, 9), (0, 9));
        var draft = Warp.ToQuad(src, (0, 0), (9, 0), (9, 9), (0, 9), maxSamples: 1);
        byte f = fine.Bgra[(4 * 9 + 4) * 4], d = draft.Bgra[(4 * 9 + 4) * 4];
        Assert.InRange(f, 100, 160);
        Assert.Equal(fine.Bounds, draft.Bounds);
        Assert.Equal(0, d);
    }

    private static void AssertNear((double X, double Y) expected, (double X, double Y) actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
    }

    // ---- 変形 ----

    [Fact]
    public void WarpWithoutDeformKeepsPixels()
    {
        // 4 隅が元の大きさのまま (30, 40) へ動くだけなら、絵はそのまま、置き場所が (30, 40)
        var src = Solid(16, 8, 10, 20, 30);
        src.Bgra[(3 * 16 + 5) * 4 + 2] = 200;
        var r = Warp.ToQuad(src, (30, 40), (46, 40), (46, 48), (30, 48));
        Assert.Equal(new PsdRect(30, 40, 46, 48), r.Bounds);
        Assert.Equal(src.Bgra, r.Bgra);
    }

    [Fact]
    public void WarpIntoTrapezoidLeavesOutsideTransparent()
    {
        // 上の辺が短い台形: 左上の隅の外は透明、真ん中は不透明
        var r = Warp.ToQuad(Solid(100, 100, 0, 0, 255), (40, 0), (60, 0), (100, 100), (0, 100));
        Assert.Equal(new PsdRect(0, 0, 100, 100), r.Bounds);
        Assert.Equal(0, Alpha(r, 2, 2));
        Assert.Equal(255, Alpha(r, 50, 50));
        // 透明な所の色が縁ににじまない（不透明度で割り戻す）
        int o = (99 * 100 + 50) * 4;
        Assert.Equal(255, r.Bgra[o + 2]);
    }

    [Fact]
    public void DigitFramesAreWarpedOneByOne()
    {
        // 3 コマ（高さ 10）の帯。コマごとに色を変える
        var src = Solid(20, 30, 0, 0, 0);
        for (int f = 0; f < 3; f++)
            for (int y = f * 10; y < f * 10 + 10; y++)
                for (int x = 0; x < 20; x++) src.Bgra[(y * 20 + x) * 4 + 1] = (byte)(50 + f * 80);
        var (strip, interval) = Warp.FramesToQuad(src, 10, (100, 200), (140, 200), (140, 220), (100, 220));
        Assert.Equal(20, interval);
        Assert.Equal(new PsdRect(100, 200, 140, 260), strip.Bounds);
        // 各コマの真ん中の色が、元のコマの色のまま
        for (int f = 0; f < 3; f++)
            Assert.Equal(50 + f * 80, strip.Bgra[((f * interval + 10) * 40 + 20) * 4 + 1]);
    }

    [Fact]
    public void DigitFramesIgnoreRemainder()
    {
        // 高さ 35 を 1 コマ 10 で割ると 3 コマと余り 5。余りは使わず、3 コマだけ並べ直す
        var src = Solid(20, 35, 0, 0, 0);
        for (int y = 30; y < 35; y++)
            for (int x = 0; x < 20; x++) src.Bgra[(y * 20 + x) * 4 + 2] = 255; // 余りの所だけ赤
        var (strip, interval) = Warp.FramesToQuad(src, 10, (0, 0), (20, 0), (20, 10), (0, 10));
        Assert.Equal(10, interval);
        Assert.Equal(30, strip.Height);
        Assert.DoesNotContain(Enumerable.Range(0, strip.Width * strip.Height), i => strip.Bgra[i * 4 + 2] > 0);
    }

    [Theory]
    // 極端に小さい 1 コマの高さで、コマ数がとても多い（高さ 100000 を 1 コマ 1 で 100000 コマ、変形後 1 コマ 10）
    [InlineData(10, 100000, 1, 10, 10)]
    // 変形後の 1 コマが大きい（8 コマ、変形後 1 コマ 8192 画素四方）
    [InlineData(8, 64, 8, 8192, 8192)]
    // コマ数が多く、変形後の 1 コマもそこそこ大きい（1000 コマ × 高さ 40）
    [InlineData(20, 20000, 20, 300, 40)]
    public void DigitFramesTooTallAreRejectedBeforeWarping(int w, int h, int interval, double qw, double qh)
    {
        // 並べた絵の高さが 16384 画素を超えるなら、配列を作る前・コマを変形する前に断る（すぐ戻る）
        var src = new PsdPixels(new PsdRect(0, 0, w, h), new byte[w * h * 4]);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.Throws<InvalidOperationException>(() => Warp.FramesToQuad(src, interval, (0, 0), (qw, 0), (qw, qh), (0, qh)));
        Assert.Contains("大きすぎます", ex.Message);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void DigitFramesUpToStripLimitAreWarped()
    {
        // ちょうど上限（1 コマ 16 画素 × 1024 コマ = 16384）は並べられる
        var src = Solid(1, 1024, 0, 0, 0);
        var (strip, interval) = Warp.FramesToQuad(src, 1, (0, 0), (1, 0), (1, 16), (0, 16));
        Assert.Equal(16, interval);
        Assert.Equal(Warp.MaxStripHeight, strip.Height);
        Assert.Equal(strip.Width * strip.Height * 4, strip.Bgra.Length);
    }

    // ---- パネルファイルの一部 ----

    private const string Master = """
        BveTs Instrument Panel 2.00

        [This]
        Resolution = 1024
        Left = 0
        Top = 0
        Right = 1024
        Bottom = 768

        ;===== グループ: モニタ =====
        ;速度計
        [Needle]
        Subject = kmph
        Location = 100, 100
        DaytimeImage = n.png
        Layer = 2
        ;===== グループ終わり: モニタ =====
        """;

    // モニタくんの書き出しと同じ形（見出しの行がない）。中のグループは master と同じ名前
    private const string Fragment = """
        ; モニタくん で書き出したモニタの部分

        ;===== グループ: モニタ =====
        ;時計
        [DigitalNumber]
        Subject = hour
        Location = 10, 20
        DaytimeImage = img\clock.png
        Interval = 16
        Layer = 3

        ;===== グループ: 文字 =====
        ;次駅
        [PilotLamp]
        Subject = ats20
        Location = 30, 40
        DaytimeImage = img\next.png
        Layer = 3
        ;===== グループ終わり: 文字 =====
        ;===== グループ終わり: モニタ =====
        """;

    [Fact]
    public void FragmentWithoutHeaderKeepsFirstLines()
    {
        var frag = PanelFragment.Parse(Fragment);
        Assert.Equal(2, frag.Sections.Count);
        Assert.False(PanelFragment.HasThis(frag));
        Assert.Equal("時計", frag.Sections[0].Title);
    }

    [Fact]
    public void FragmentWithThisIsRejected()
    {
        Assert.True(PanelFragment.HasThis(PanelFragment.Parse(Master)));
        // 見出しの行がなくても [This] があれば取り込まない
        Assert.True(PanelFragment.HasThis(PanelFragment.Parse("[This]\nResolution = 1024\n")));
    }

    [Fact]
    public void FragmentBecomesOneGroupWithRenamedInnerGroups()
    {
        var master = PanelDocument.Parse(Master);
        var p = PanelFragment.Prepare(master, PanelFragment.Parse(Fragment), "monitor_panel");
        Assert.Equal("monitor_panel", p.Group);
        Assert.Equal(["monitor_panel", "モニタ 2"], p.Sections[0].Groups);
        Assert.Equal(["monitor_panel", "モニタ 2", "文字"], p.Sections[1].Groups);

        // 足して書き出し、読み直してもグループが保たれる
        master.Sections.AddRange(p.Sections);
        var again = PanelDocument.Parse(master.Serialize());
        Assert.Equal(4, again.Sections.Count);
        Assert.Equal(["モニタ"], again.Sections[1].Groups);
        Assert.Equal(["monitor_panel", "モニタ 2", "文字"], again.Sections[3].Groups);
        Assert.Equal("img\\next.png", again.Sections[3].Get("DaytimeImage"));
        Assert.Empty(again.GroupWarnings);
    }

    [Fact]
    public void FragmentGroupNameAvoidsExistingNames()
    {
        var master = PanelDocument.Parse(Master);
        var p = PanelFragment.Prepare(master, PanelFragment.Parse(Fragment), "モニタ");
        Assert.Equal("モニタ 2", p.Group);
        Assert.Equal(["モニタ 2", "モニタ 3"], p.Sections[0].Groups);
    }

    [Fact]
    public void FragmentDropsMainPanelMark()
    {
        var frag = PanelFragment.Parse("[PilotLamp]\n;@メインパネル\nSubject = true\nLocation = 0, 0\nDaytimeImage = base.png\nLayer = 1\n");
        Assert.True(frag.Sections[0].IsMainPanel);
        var p = PanelFragment.Prepare(PanelDocument.Parse(Master), frag, "部分");
        Assert.False(p.Sections[0].IsMainPanel);
        Assert.NotEmpty(p.Notes);
        Assert.Equal(["base.png"], PanelFragment.ImageValues(p.Sections[0]).Select(v => v.Value));
    }

    [Fact]
    public void FragmentDropsVariationMarks()
    {
        // 取り込むファイルのバリエーション「夜間」は、取り込み先にはない（バリエーションの一覧は取り込まない）
        var frag = PanelFragment.Parse("""
            ;@バリエーション: 夜間 = night.txt
            ; @group 夜間
            [PilotLamp]
            Subject = ats10
            Location = 0, 0
            DaytimeImage = lamp.png
            Layer = 2
            ; @end
            [PilotLamp]
            Subject = ats11
            Location = 10, 0
            DaytimeImage = lamp2.png
            Layer = 2
            """);
        Assert.Equal("夜間", frag.Sections[0].VariationCond);
        var master = PanelDocument.Parse(Master);
        var p = PanelFragment.Prepare(master, frag, "部分");
        Assert.All(p.Sections, s => Assert.Null(s.VariationCond));
        Assert.Contains(p.Notes, n => n.Contains("バリエーション"));
        // 取り込み先に足して書き出しても、バリエーションの印は出ない（どのバリエーションにも入る）
        master.Sections.AddRange(p.Sections);
        var text = master.Serialize();
        Assert.DoesNotContain("@group", text);
        Assert.DoesNotContain("夜間", text);
        Assert.All(PanelDocument.Parse(text).Sections, s => Assert.Null(s.VariationCond));
    }

    [Fact]
    public void FragmentKeepsWarpInfoAndItCanBeRebased()
    {
        // モニタくんの書き出し先（C:\mk\out）の部分に、変形の方法の行がある
        var frag = PanelFragment.Parse("""
            [PilotLamp]
            ;@変形: 元=src\monitor.png | 隅=0,0 10,0 10,10 0,10
            Subject = true
            Location = 0, 0
            DaytimeImage = warp\monitor_warp.png
            Layer = 2
            """);
        var p = PanelFragment.Prepare(PanelDocument.Parse(Master), frag, "部分");
        var s = p.Sections[0];
        Assert.Equal("src\\monitor.png", s.Warp?.Source);
        // パネルのフォルダ（C:\work\panel）から見た場所に直す
        PanelDocument.RebaseWarp(s, @"C:\mk\out", @"C:\work\panel");
        Assert.Equal(@"..\..\mk\out\src\monitor.png", s.Warp!.Source);
        Assert.Equal((10, 10), s.Warp.Corners[2]);
    }

    // ---- 変形の方法を覚える ----

    private const string Warped = """
        BveTs Instrument Panel 2.00

        [This]
        Resolution = 1024

        ;monitor
        [PilotLamp]
        ;@変形: 元=src\monitor.svg | 夜=src\monitor_N.svg | 隅=210,110 320,125.5 318,215 205,222 | 倍率=2
        Subject = true
        Location = 205, 110
        DaytimeImage = warp\monitor_warp.png
        Layer = 1

        [DigitalNumber]
        ;@変形: 元=..\parts\digits.png | 隅=0,0 40,4 40,24 0,20 | コマ=10
        Subject = ats99
        Location = 0, 0
        DaytimeImage = warp\digits_warp.png
        Interval = 24
        Layer = 1
        """;

    [Fact]
    public void WarpInfoIsReadAndWrittenBack()
    {
        var doc = PanelDocument.Parse(Warped);
        var w = doc.Sections[1].Warp;
        Assert.NotNull(w);
        Assert.Equal("src\\monitor.svg", w!.Source);
        Assert.Equal("src\\monitor_N.svg", w.Night);
        Assert.Equal((320, 125.5), w.Corners[1]);
        Assert.Equal(2, w.SvgScale);
        Assert.Equal(0, w.Interval);
        Assert.Equal(10, doc.Sections[2].Warp!.Interval);
        Assert.Null(doc.Sections[2].Warp!.Night);
        // 中身の行には入らない（キーとして扱わない）
        Assert.DoesNotContain(doc.Sections[1].Lines, l => l.Raw.Contains("@変形"));
        // そのまま書き戻せる
        Assert.Equal(Warped.Replace("\r\n", "\n"), doc.Serialize().Replace("\r\n", "\n").TrimEnd('\n'));
        // 複製しても別のもの
        var c = doc.Sections[1].Clone();
        c.Warp!.Corners[0] = (1, 1);
        Assert.Equal((210, 110), doc.Sections[1].Warp!.Corners[0]);
    }

    [Fact]
    public void BrokenWarpLineStaysAsComment()
    {
        var doc = PanelDocument.Parse("BveTs Instrument Panel 2.00\n[PilotLamp]\n;@変形: 元=a.png | 隅=1,2 3,4\nSubject = true\n");
        Assert.Null(doc.Sections[0].Warp);
        Assert.Contains(doc.Sections[0].Lines, l => l.Raw.Contains("@変形"));
    }

    [Fact]
    public void WarpSourceIsRebasedWhenExportedElsewhere()
    {
        var doc = PanelDocument.Parse(Warped);
        var s = doc.Sections[1];
        PanelDocument.RebaseWarp(s, @"C:\work\panel", @"C:\work\panel\out");
        Assert.Equal(@"..\src\monitor.svg", s.Warp!.Source);
        Assert.Equal(@"..\src\monitor_N.svg", s.Warp.Night);
    }

    // ---- SVG ----

    [Fact]
    public void SvgExternalReferencesAreRemoved()
    {
        var svg = """<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="10" height="10"><image xlink:href="http://example.com/a.png" width="10" height="10"/><rect width="10" height="10" fill="url(#g)"/></svg>""";
        var bytes = Encoding.UTF8.GetBytes(svg);
        Assert.True(SvgSafety.LooksLikeSvg(bytes));
        var clean = Encoding.UTF8.GetString(SvgSafety.StripExternal(bytes, out int removed));
        Assert.Equal(1, removed);
        Assert.DoesNotContain("example.com", clean);
        Assert.Contains("url(#g)", clean);
    }

    [Theory]
    // style 属性・style 要素の url()・@import
    [InlineData("""<rect width="1" height="1" style="fill: url(http://example.com/p.svg#g)"/>""")]
    [InlineData("""<style>@import "http://example.com/a.css"; rect { fill: red }</style>""")]
    [InlineData("""<style>rect { fill: url('file:///C:/example.com/p.svg#g') }</style>""")]
    // CSS のエスケープで書いた url( と @import
    [InlineData("""<rect width="1" height="1" style="fill: \75 rl(http://example.com/p.svg#g)"/>""")]
    [InlineData("""<rect width="1" height="1" style="fill: u\72l(http://example.com/p.svg#g)"/>""")]
    [InlineData("""<style>rect { fill: \55 \52 \4c (http://example.com/p.svg#g) }</style>""")]
    [InlineData("""<style>@\69mport "http://example.com/a.css";</style>""")]
    // 文字列で場所を書く関数
    [InlineData("""<style>rect { background-image: image-set("http://example.com/a.png" 1x) }</style>""")]
    [InlineData("""<style>rect { fill: src("http://example.com/p.svg#g") }</style>""")]
    // 表示の属性（fill・filter など）の url()
    [InlineData("""<rect width="1" height="1" filter="url(http://example.com/f.svg#f)"/>""")]
    [InlineData("""<rect width="1" height="1" fill="\75 rl(http://example.com/p.svg#g)"/>""")]
    // href（use・feImage）
    [InlineData("""<use href="http://example.com/p.svg#g"/>""")]
    [InlineData("""<filter id="f"><feImage href="http://example.com/a.png"/></filter>""")]
    public void SvgExternalReferencesInCssAreRemoved(string body)
    {
        var svg = $"""<?xml version="1.0"?><?xml-stylesheet href="http://example.com/x.css"?><svg xmlns="http://www.w3.org/2000/svg" width="10" height="10">{body}<rect width="1" height="1" fill="url(#keep)"/></svg>""";
        var clean = Encoding.UTF8.GetString(SvgSafety.StripExternal(Encoding.UTF8.GetBytes(svg), out int removed));
        Assert.True(removed >= 2); // xml-stylesheet と本文の参照
        Assert.DoesNotContain("example.com", clean);
        Assert.DoesNotContain("xml-stylesheet", clean);
        Assert.Contains("url(#keep)", clean);
        Assert.Contains("http://www.w3.org/2000/svg", clean); // 名前空間はそのまま
    }

    [Fact]
    public void SvgDataUriAndEscapesWithoutReferencesAreKept()
    {
        // data: と文書の中の参照は残す。外を指さないエスケープは書き換えない
        var svg = """<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><style>.a\:b { fill: url(#g) }</style><image href="data:image/png;base64,AAAA" width="1" height="1"/><rect style="fill: url( 'data:image/png;base64,AAAA' )"/></svg>""";
        var bytes = Encoding.UTF8.GetBytes(svg);
        Assert.Same(bytes, SvgSafety.StripExternal(bytes, out int removed));
        Assert.Equal(0, removed);
    }

    [Theory]
    // image-set()・image()・src() の中の、文書の中の参照・data: は残す（変えない）
    [InlineData("""background-image: image-set("data:image/png;base64,AAAA" 1x)""")]
    [InlineData("""background-image: image-set(url(#local) 1x)""")]
    [InlineData("""background-image: image-set(url("data:image/png;base64,AAAA") 1x, "#b" 2x)""")]
    [InlineData("""fill: image("#local")""")]
    [InlineData("""background-image: image-set("data:image/png;base64,AAAA" type("image/png") 1x)""")]
    [InlineData("""fill: src("#local")""")]
    public void SvgStringUrlFunctionsKeepLocalReferences(string css)
    {
        var svg = $"""<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><rect width="1" height="1" style='{css}'/></svg>""";
        var bytes = Encoding.UTF8.GetBytes(svg);
        Assert.Same(bytes, SvgSafety.StripExternal(bytes, out int removed));
        Assert.Equal(0, removed);
    }

    [Theory]
    // 外を指す文字列だけを空にし、ほかの候補・type()・関数の形は残す
    [InlineData("""image-set("http://example.com/a.png" 1x, "data:image/png;base64,AAAA" 2x)""", """image-set("" 1x, "data:image/png;base64,AAAA" 2x)""")]
    [InlineData("""image-set('http://example.com/a.png' type("image/png") 1x, url(#b) 2x)""", """image-set('' type("image/png") 1x, url(#b) 2x)""")]
    [InlineData("""image("http://example.com/a.png", "#local")""", """image("", "#local")""")]
    [InlineData("""-webkit-image-set("http://example.com/a.png" 1x)""", """-webkit-image-set("" 1x)""")]
    // 文字列の中のエスケープ・注釈の中の括弧でも見落とさない
    [InlineData("""image-set("\68ttp://example.com/a.png" 1x)""", """image-set("" 1x)""")]
    [InlineData("""image-set(/*)*/ "http://example.com/a.png" 1x)""", """image-set(/*)*/ "" 1x)""")]
    public void SvgStringUrlFunctionsDropOnlyExternalStrings(string css, string expected)
    {
        var svg = $"""<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><style>rect {"{"} background-image: {css} {"}"}</style></svg>""";
        var clean = Encoding.UTF8.GetString(SvgSafety.StripExternal(Encoding.UTF8.GetBytes(svg), out int removed));
        Assert.Equal(1, removed);
        Assert.DoesNotContain("example.com", clean);
        Assert.Contains(expected, clean);
    }

    [Fact]
    public void SvgEmbeddedSvgImageIsRemoved()
    {
        // 中に埋め込んだ SVG 画像は取り除く（その中の参照までは確かめられないため）。PNG の data: は残す
        var inner = Convert.ToBase64String(Encoding.UTF8.GetBytes("""<svg xmlns="http://www.w3.org/2000/svg"><image href="http://example.com/a.png"/></svg>"""));
        var svg = $"""<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><image href=" data:image/svg+xml;base64,{inner}" width="5" height="5"/><image href="data:image/png;base64,AAAA" width="5" height="5"/></svg>""";
        var clean = Encoding.UTF8.GetString(SvgSafety.StripExternal(Encoding.UTF8.GetBytes(svg), out int removed));
        Assert.Equal(1, removed);
        Assert.DoesNotContain(inner, clean);
        Assert.Contains("data:image/png;base64,AAAA", clean);
    }

    [Theory]
    [InlineData(@"\75 rl(", "url(")]
    [InlineData(@"u\72l(", "url(")]
    [InlineData(@"\000075rl(", "url(")]
    [InlineData("\\75\r\nrl(", "url(")]
    [InlineData(@"\@import", "@import")]
    [InlineData(@"a\:b", "a:b")]
    public void CssEscapesAreUnescaped(string css, string plain) => Assert.Equal(plain, SvgSafety.UnescapeCss(css));
}
