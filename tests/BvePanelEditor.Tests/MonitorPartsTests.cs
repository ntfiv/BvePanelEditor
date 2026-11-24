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
}
