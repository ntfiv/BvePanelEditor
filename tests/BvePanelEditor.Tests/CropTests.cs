using BvePanelEditor.Core;
using BvePanelEditor.Core.Crop;
using BvePanelEditor.Core.Psd;
using Xunit;

namespace BvePanelEditor.Tests;

public sealed class CropTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "panelkun_crop_" + Guid.NewGuid().ToString("N"));
    public CropTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    /// <summary>暗い板の上に、四角の灯を描いた画像。</summary>
    private static PsdPixels Panel(int w, int h, byte bg, params (PsdRect R, byte Red, byte Green, byte Blue)[] lamps)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = bg; px[i + 3] = 255; }
        foreach (var (r, cr, cg, cb) in lamps)
            for (int y = r.Top; y < r.Bottom; y++)
                for (int x = r.Left; x < r.Right; x++)
                {
                    int i = (y * w + x) * 4;
                    px[i] = cb; px[i + 1] = cg; px[i + 2] = cr;
                }
        return new PsdPixels(new PsdRect(0, 0, w, h), px);
    }

    [Fact]
    public void NearerIsLit_PicksTheImageThePartWasMadeFrom()
    {
        var unlit = Panel(80, 60, 30);
        var lit = Panel(80, 60, 30, (A, 255, 200, 0), (B, 255, 200, 0), (C, 0, 255, 0));
        var sw = new PsdRect(40, 20, 50, 30); // スイッチの所だけ変えた部品用の画像
        var partFromLit = Panel(80, 60, 30, (A, 255, 200, 0), (B, 255, 200, 0), (C, 0, 255, 0), (sw, 200, 200, 200));
        var partFromUnlit = Panel(80, 60, 30, (sw, 200, 200, 200));
        Assert.Equal(100, RegionDetect.CountDiff(partFromUnlit, unlit, 8));
        Assert.True(RegionDetect.NearerIsLit(partFromLit, unlit, lit, 8));
        Assert.False(RegionDetect.NearerIsLit(partFromUnlit, unlit, lit, 8));
        Assert.False(RegionDetect.NearerIsLit(partFromLit, unlit, null, 8));
        // 近い方と比べると、変えた所（スイッチ）だけが見つかる。無発光と比べると表示灯も拾う
        Assert.Equal([new PsdRect(40, 20, 50, 30)], RegionDetect.Detect(lit, partFromLit, threshold: 8, gap: 2, pad: 0));
        Assert.True(RegionDetect.Detect(unlit, partFromLit, threshold: 8, gap: 2, pad: 0).Count >= 3);
    }

    // 灯 A・B は 4px あけて横に並ぶ（まとめる距離 6 なら 1 つ、0〜1 なら 2 つ）。C は離れている
    static readonly PsdRect A = new(10, 10, 20, 16), B = new(24, 10, 34, 16), C = new(60, 40, 70, 50);

    private static (PsdPixels Unlit, PsdPixels Lit) Pair(byte bg) =>
        (Panel(100, 60, bg, (A, 60, 20, 20), (B, 20, 60, 20), (C, 60, 60, 20)),
         Panel(100, 60, bg, (A, 255, 40, 40), (B, 40, 255, 40), (C, 255, 255, 40)));

    [Fact]
    public void DetectFindsLampsAndMergesNearOnes()
    {
        var (u, l) = Pair(20);
        var merged = RegionDetect.Detect(u, l, threshold: 24, gap: 6, pad: 0);
        Assert.Equal(2, merged.Count);
        Assert.Equal(new PsdRect(10, 10, 34, 16), merged[0]);  // A と B が 1 つに
        Assert.Equal(C, merged[1]);

        var apart = RegionDetect.Detect(u, l, threshold: 24, gap: 1, pad: 0);
        Assert.Equal([A, B, C], apart);
    }

    [Fact]
    public void SplitAndUnion()
    {
        var (u, l) = Pair(20);
        var parts = RegionDetect.Split(u, l, new PsdRect(8, 8, 36, 18));
        Assert.Equal(2, parts.Count);
        Assert.True(parts[0].Left <= A.Left && parts[0].Right >= A.Right && parts[0].Right < B.Left);
        Assert.Equal(new PsdRect(10, 10, 70, 50), RegionDetect.Union([A, C]));
        // 分けられないものはそのまま
        Assert.Equal([C], RegionDetect.Split(u, l, C));
    }

    [Fact]
    public void ThresholdIgnoresSmallDifferences()
    {
        var u = Panel(40, 20, 50);
        var l = Panel(40, 20, 60, (new PsdRect(5, 5, 15, 15), 200, 200, 200)); // 全体が 10 だけ明るい＋灯
        var r = RegionDetect.Detect(u, l, threshold: 24, gap: 2, pad: 0);
        Assert.Equal([new PsdRect(5, 5, 15, 15)], r);
    }

    private CropBuildSpec Spec(out PsdPixels litDay, out PsdPixels litNight)
    {
        var (u, l) = Pair(20);
        litDay = l;
        litNight = Panel(100, 60, 5, (A, 255, 100, 100), (B, 100, 255, 100), (C, 255, 255, 100));
        return new CropBuildSpec
        {
            PanelDir = _dir, OutputDir = Path.Combine(_dir, "crop"),
            UnlitDayPath = Path.Combine(_dir, "src", "panel.png"), UnlitNightPath = Path.Combine(_dir, "src", "panel_N.png"),
            Width = 100, Height = 60,
            Sets = [new CropSetImages { Name = "発光", DayPath = Path.Combine(_dir, "src", "panel_TT.png"), Day = litDay, NightPath = Path.Combine(_dir, "src", "panel_N_TT.png"), Night = litNight }],
            Regions =
            [
                new CropRegion { Rect = A, Name = "戸", Subject = "door", Layer = 5 },
                new CropRegion { Rect = B, Name = "戸_N", Subject = "ats1" },
                new CropRegion { Rect = C, Name = "", Subject = "" },
            ],
        };
    }

    [Fact]
    public void BuildCropsRectanglesAndNamesFiles()
    {
        var r = CropBuild.Build(Spec(out var litDay, out _));
        Assert.Equal(Path.Combine("src", "panel.png"), r.BaseDay);
        Assert.Equal(Path.Combine("src", "panel_N.png"), r.BaseNight);
        Assert.Equal(4, r.Sections.Count);                  // メインパネル＋表示灯 3
        Assert.True(r.Sections[0].IsMainPanel);
        Assert.Equal(Path.Combine("src", "panel.png"), r.Sections[0].Get("DaytimeImage"));
        Assert.Equal(6, r.Files.Count);                     // 表示灯 3×昼夜（[This] の画像は作らない）
        Assert.Equal(r.Files.Count, r.Files.Select(f => f.FullPath.ToLowerInvariant()).Distinct().Count()); // 「戸_N」と「戸」の夜がぶつからない

        var door = r.Sections[1];
        Assert.Equal("戸", door.Title);
        Assert.Equal("door", door.Get("Subject"));
        Assert.Equal("10, 10", door.Get("Location"));
        Assert.Equal("5", door.Get("Layer"));
        Assert.Equal("発光", door.CropSet);
        Assert.Equal(Path.Combine("crop", "戸.png"), door.Get("DaytimeImage"));
        Assert.Equal(Path.Combine("crop", "戸_N.png"), door.Get("NighttimeImage"));
        var doorFile = r.Files.First(f => f.FullPath.EndsWith(Path.Combine("crop", "戸.png")));
        var png = TestPngReader.Read(doorFile.Png);
        Assert.Equal((16, 8), (png.W, png.H));          // 10×6 を 2 の累乗に広げる
        Assert.Equal(0, png.Rgba[(0 * 16 + 10) * 4 + 3]);   // 広げた所（右）は透明
        Assert.Equal(0, png.Rgba[(6 * 16 + 0) * 4 + 3]);    // 広げた所（下）も透明
        Assert.Equal(255, png.Rgba[(5 * 16 + 9) * 4 + 3]);  // 絵は左上に置く
        Assert.Equal((255, 40, 40, 255), (png.Rgba[0], png.Rgba[1], png.Rgba[2], png.Rgba[3]));
        Assert.Equal("表示灯3", r.Sections[3].Title);   // 名前がなければ番号
        Assert.True(PngWriter.IsPanelKunPngBytes(doorFile.Png));
    }

    [Fact]
    public void CropLinksRoundTrip()
    {
        var r = CropBuild.Build(Spec(out _, out _));
        var doc = PanelDocument.CreateNew(null, 100, 60);
        doc.CropSets.AddRange(r.Sets);
        doc.CropSets.Add(new CropSet { Name = "扉", Day = "door.png" });
        doc.PsdDay = "a.psd";
        foreach (var s in r.Sections) doc.Sections.Add(s);
        doc.MakeGroup([2, 3], "灯");
        var text = doc.Serialize();
        Assert.Contains($";===== 切り抜き元 発光: 昼={Path.Combine("src", "panel_TT.png")} | 夜={Path.Combine("src", "panel_N_TT.png")} =====", text);
        Assert.Contains(";===== 切り抜き元 扉: 昼=door.png =====", text);
        Assert.Contains("[PilotLamp]\r\n;@切り抜き: 発光\r\n", text);
        Assert.Contains(";メインパネル\r\n[PilotLamp]\r\n;@メインパネル\r\n", text);
        var back = PanelDocument.Parse(text);
        Assert.Equal(text, back.Serialize());
        Assert.Equal(2, back.CropSets.Count);
        Assert.Null(back.CropSets[1].Night);
        Assert.Equal("発光", back.Sections[2].CropSet);
        Assert.Equal("戸", back.Sections[2].Title);
        Assert.Same(back.Sections[1], back.MainPanel);
        Assert.True(back.HasCropLink);
        Assert.Empty(back.GroupWarnings);
        Assert.DoesNotContain(back.DuplicateGroup("灯", "灯 のコピー", 1, 1).Indices, i => back.Sections[i].CropSet != null);
    }

    [Fact]
    public void UpdateRecropsAndSkipsReplacedImages()
    {
        var spec = Spec(out var litDay, out var litNight);
        var r = CropBuild.Build(spec);
        foreach (var f in r.Files) { Directory.CreateDirectory(Path.GetDirectoryName(f.FullPath)!); File.WriteAllBytes(f.FullPath, f.Png); }
        var doc = PanelDocument.CreateNew(null, 100, 60);
        doc.CropSets.AddRange(r.Sets);
        foreach (var s in r.Sections) doc.Sections.Add(s);

        // 位置を 2px 動かして更新すると、新しい位置で切り抜き直す（[This]・メインパネルは元の画像を指すだけなので触らない）
        doc.Sections[2].Set("Location", "12, 10");
        var images = new Dictionary<string, PsdPixels>
        {
            [spec.Sets[0].DayPath] = litDay, [spec.Sets[0].NightPath!] = litNight,
        };
        var plan = CropUpdate.Plan(doc, _dir, p => images.GetValueOrDefault(Path.GetFullPath(p)));
        Assert.Equal(3, plan.Linked);
        Assert.Equal(6, plan.Files.Count);
        Assert.Empty(plan.Missing);
        var moved = TestPngReader.Read(plan.Files.First(f => f.FullPath.EndsWith(Path.Combine("crop", "戸.png"))).Png);
        Assert.Equal((16, 8), (moved.W, moved.H));        // 中身は 10×6 のまま（PNG に書いた大きさで切り出す）
        Assert.Equal(0, moved.Rgba[(0 * 16 + 10) * 4 + 3]);
        Assert.Equal(255, moved.Rgba[0]);                    // 左端は灯 A（x=12）
        Assert.Equal(20, moved.Rgba[(0 * 16 + 8) * 4]);       // 右の 2px は板（x=20, 21）

        // 手で差し替えた画像（印なし）は飛ばす
        var lampPng = Path.Combine(_dir, doc.Sections[4].Get("DaytimeImage")!);
        var plain = File.ReadAllBytes(lampPng);
        File.WriteAllBytes(lampPng, StripText(plain));
        var plan2 = CropUpdate.Plan(doc, _dir, p => images.GetValueOrDefault(Path.GetFullPath(p)));
        Assert.Single(plan2.Skipped);
        Assert.Equal(4, plan2.Files.Count);

        // 範囲が元画像からはみ出すと知らせる
        doc.Sections[2].Set("Location", "95, 10");
        var plan3 = CropUpdate.Plan(doc, _dir, p => images.GetValueOrDefault(Path.GetFullPath(p)));
        Assert.Contains(plan3.Warnings, w => w.Contains("はみ出し"));
    }

    private static byte[] StripText(byte[] png)
    {
        var o = new MemoryStream();
        o.Write(png, 0, 8);
        int pos = 8;
        while (pos < png.Length)
        {
            int len = png[pos] << 24 | png[pos + 1] << 16 | png[pos + 2] << 8 | png[pos + 3];
            if (System.Text.Encoding.ASCII.GetString(png, pos + 4, 4) != "tEXt") o.Write(png, pos, 12 + len);
            pos += 12 + len;
        }
        return o.ToArray();
    }
}
