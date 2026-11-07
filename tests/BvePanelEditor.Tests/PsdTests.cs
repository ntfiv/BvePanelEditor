using BvePanelEditor.Core;
using BvePanelEditor.Core.Psd;
using Xunit;

namespace BvePanelEditor.Tests;

public sealed class PsdTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "panelkun_psd_" + Guid.NewGuid().ToString("N"));

    public PsdTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string WritePsd(string name, int w, int h, params TestLayer[] layers)
    {
        var p = Path.Combine(_dir, name);
        TestPsdWriter.Write(p, w, h, layers);
        return p;
    }

    private static (byte R, byte G, byte B, byte A) At(PsdPixels px, int cx, int cy)
    {
        int i = ((cy - px.Bounds.Top) * px.Width + (cx - px.Bounds.Left)) * 4;
        return (px.Bgra[i + 2], px.Bgra[i + 1], px.Bgra[i], px.Bgra[i + 3]);
    }

    [Fact]
    public void PackBitsRoundTrip()
    {
        var src = new byte[] { 1, 1, 1, 1, 2, 3, 4, 4, 4, 5, 6, 6, 7 };
        var packed = TestPsdWriter.PackBits(src);
        Assert.True(packed.Length < src.Length + 4);
    }

    [Fact]
    public void ReadsLayersFoldersAndNames()
    {
        var p = WritePsd("a.psd", 64, 32,
            TestLayer.Solid("背景", new PsdRect(0, 0, 64, 32), 0, 0, 255),
            TestLayer.FolderEnd(),
            TestLayer.Solid("ats22 次駅停車", new PsdRect(4, 4, 12, 10), 255, 200, 0),
            new TestLayer { Name = "隠れ", Bounds = new PsdRect(20, 4, 24, 8), Bgra = new byte[4 * 4 * 4], Hidden = true },
            TestLayer.FolderStart("表示灯"));
        using var psd = PsdFile.Open(p);
        Assert.Equal(64, psd.Width);
        Assert.Equal(5, psd.Layers.Count);
        var lamp = psd.Find("表示灯/ats22 次駅停車")!;
        Assert.NotNull(lamp);
        Assert.Equal("表示灯", lamp.Parent!.Name);
        Assert.Equal(new PsdRect(4, 4, 12, 10), lamp.Bounds);
        Assert.True(psd.Find("隠れ")!.Hidden);  // レイヤー名だけでも見つかる
        Assert.Equal(PsdLayerKind.FolderOpen, psd.Find("表示灯")!.Kind);
        Assert.Equal(3, psd.PixelLayers.Count());
    }

    [Fact]
    public void RawAndRleDecodeTheSame()
    {
        var px = new byte[10 * 6 * 4];
        var rnd = new Random(1);
        rnd.NextBytes(px);
        for (int i = 0; i < 20; i++) px[i] = 7; // 繰り返しも入れる
        var p1 = WritePsd("raw.psd", 16, 16, new TestLayer { Name = "x", Bounds = new PsdRect(3, 2, 13, 8), Bgra = px, Rle = false });
        var p2 = WritePsd("rle.psd", 16, 16, new TestLayer { Name = "x", Bounds = new PsdRect(3, 2, 13, 8), Bgra = px, Rle = true });
        using var a = PsdFile.Open(p1);
        using var b = PsdFile.Open(p2);
        Assert.Equal(px, a.GetPixels(a.Layers[0])!.Bgra);
        Assert.Equal(px, b.GetPixels(b.Layers[0])!.Bgra);
    }

    [Fact]
    public void ComposeNormalOpacityAndCanvasClip()
    {
        var top = TestLayer.Solid("上", new PsdRect(-4, -4, 8, 8), 255, 0, 0); // キャンバスの外へはみ出す
        top.Opacity = 128;
        var p = WritePsd("c.psd", 16, 16, TestLayer.Solid("下", new PsdRect(0, 0, 16, 16), 0, 0, 255), top);
        using var psd = PsdFile.Open(p);
        var img = PsdCompose.Compose(psd, psd.PixelLayers, psd.Canvas);
        Assert.Equal(16, img.Width);
        var c = At(img, 2, 2);
        Assert.InRange(c.R, 125, 130);
        Assert.InRange(c.B, 125, 130);
        Assert.Equal(255, c.A);
        Assert.Equal((byte)0, At(img, 10, 10).R);
    }

    [Fact]
    public void MultiplyAndScreen()
    {
        var mul = TestLayer.Solid("乗算", new PsdRect(0, 0, 4, 4), 128, 255, 0);
        mul.Blend = "mul ";
        var scr = TestLayer.Solid("スクリーン", new PsdRect(4, 0, 8, 4), 128, 0, 0);
        scr.Blend = "scrn";
        var p = WritePsd("m.psd", 8, 4, TestLayer.Solid("下", new PsdRect(0, 0, 8, 4), 200, 100, 50), mul, scr);
        using var psd = PsdFile.Open(p);
        var img = PsdCompose.Compose(psd, psd.PixelLayers, psd.Canvas);
        var m = At(img, 1, 1);
        Assert.InRange(m.R, 99, 102);   // 200 × 128/255
        Assert.InRange(m.G, 99, 101);   // 100 × 1
        Assert.Equal(0, m.B);
        var s = At(img, 5, 1);
        Assert.InRange(s.R, 226, 229);  // 200 + 128 − 200×128/255
    }

    [Fact]
    public void MaskAndClipping()
    {
        var masked = TestLayer.Solid("マスク付き", new PsdRect(0, 0, 8, 8), 255, 255, 255);
        masked.MaskBounds = new PsdRect(0, 0, 4, 8);
        masked.Mask = Enumerable.Repeat((byte)255, 4 * 8).ToArray(); // 左半分だけ見える（外は既定値 0）
        var clipped = TestLayer.Solid("クリップ", new PsdRect(0, 0, 8, 8), 255, 0, 0);
        clipped.Clipped = true;
        var p = WritePsd("k.psd", 8, 8, masked, clipped);
        using var psd = PsdFile.Open(p);
        var baseLayer = psd.Find("マスク付き")!;
        var px = psd.GetPixels(baseLayer)!;
        Assert.Equal(255, At(px, 1, 1).A);
        Assert.Equal(0, At(px, 6, 1).A);
        // 切り出すとクリッピングされた赤が土台の形で重なる
        var one = PsdCompose.RenderLayer(psd, baseLayer, psd.Canvas);
        Assert.Equal((255, 0, 0, 255), At(one, 1, 1));
        Assert.Equal(0, At(one, 6, 1).A);
    }

    [Fact]
    public void PngRoundTrip()
    {
        var px = new byte[5 * 3 * 4];
        new Random(3).NextBytes(px);
        for (int i = 3; i < px.Length; i += 8) px[i] = 0; // 透明な画素もある
        var png = PngWriter.Encode(5, 3, px);
        var (w, h, rgba) = TestPngReader.Read(png);
        Assert.Equal((5, 3), (w, h));
        for (int i = 0; i < 15; i++)
        {
            Assert.Equal(px[i * 4 + 3], rgba[i * 4 + 3]);
            if (px[i * 4 + 3] == 0) continue;
            Assert.Equal(px[i * 4 + 2], rgba[i * 4]);
            Assert.Equal(px[i * 4], rgba[i * 4 + 2]);
        }
    }

    // ================= 読み込み・更新 =================

    private (string Day, string Night) WriteDayNight(int lampX = 10)
    {
        var day = WritePsd("panel_d.psd", 100, 60,
            TestLayer.Solid("背景", new PsdRect(0, 0, 100, 60), 30, 30, 30),
            TestLayer.Solid("メーター", new PsdRect(50, 10, 90, 50), 200, 200, 200),
            TestLayer.FolderEnd(),
            new TestLayer { Name = "ats22 次駅停車", Bounds = new PsdRect(lampX, 10, lampX + 8, 16), Bgra = TestLayer.Solid("", new PsdRect(0, 0, 8, 6), 255, 220, 0).Bgra, Hidden = true },
            TestLayer.Solid("戸", new PsdRect(30, 10, 36, 14), 255, 120, 0),
            TestLayer.FolderStart("表示灯"),
            TestLayer.Solid("N0", new PsdRect(10, 40, 16, 50), 0, 255, 0),
            TestLayer.Solid("N1", new PsdRect(11, 41, 17, 49), 0, 200, 0),
            TestLayer.Solid("N2", new PsdRect(10, 40, 16, 50), 0, 150, 0));
        var night = WritePsd("panel_n.psd", 100, 60,
            TestLayer.Solid("背景", new PsdRect(0, 0, 100, 60), 5, 5, 10),
            TestLayer.FolderEnd(),
            new TestLayer { Name = "ats22 次駅停車", Bounds = new PsdRect(lampX - 2, 9, lampX + 8, 17), Bgra = TestLayer.Solid("", new PsdRect(0, 0, 10, 8), 255, 255, 120).Bgra, Hidden = true },
            TestLayer.Solid("夜だけ", new PsdRect(60, 30, 64, 34), 0, 0, 255),
            TestLayer.FolderStart("表示灯"),
            TestLayer.Solid("N1", new PsdRect(11, 41, 17, 49), 0, 90, 0));
        return (day, night);
    }

    private PsdImportResult Import(string dayPath, string nightPath, out PsdImportSpec spec)
    {
        var day = PsdFile.Open(dayPath);
        var night = PsdFile.Open(nightPath);
        spec = new PsdImportSpec
        {
            Day = day, Night = night, PanelDir = _dir, OutputDir = Path.Combine(_dir, "psd"),
            BaseLayers = ["背景", "メーター"],
            LampLayers = ["表示灯/ats22 次駅停車", "表示灯/戸", "夜だけ"],
            Numbers = [new PsdNumberSpec { Name = "ノッチ", Frames = ["N0", "N1", "N2"] }],
            Layer = 5,
        };
        return PsdImport.Build(spec);
    }

    [Fact]
    public void ImportBuildsSectionsAndImages()
    {
        var (d, n) = WriteDayNight();
        var r = Import(d, n, out var spec);
        using var _1 = spec.Day; using var _2 = spec.Night!;

        Assert.NotNull(r.Base);
        Assert.Equal(Path.Combine("psd", "base.png"), r.Base!.DayPath);
        Assert.Equal(Path.Combine("psd", "base_N.png"), r.Base.NightPath);
        Assert.Equal((100, 60), (r.Base.Width, r.Base.Height));

        Assert.Equal(4, r.Sections.Count);
        var lamp = r.Sections.First(s => s.Title == "ats22 次駅停車");
        Assert.Equal("ats22", lamp.Get("Subject"));
        Assert.Equal("表示灯", lamp.Group);
        Assert.Equal("8, 9", lamp.Get("Location"));  // 昼 (10,10)-(18,16) と夜 (8,9)-(18,17) を合わせた範囲
        Assert.Equal(["表示灯/ats22 次駅停車"], lamp.PsdLayers);
        var dayPng = TestPngReader.Read(r.Files.First(f => f.FullPath.EndsWith("ats22 次駅停車.png")).Png);
        var nightPng = TestPngReader.Read(r.Files.First(f => f.FullPath.EndsWith("ats22 次駅停車_N.png")).Png);
        Assert.Equal((16, 8), (dayPng.W, dayPng.H));     // 10×8 を 2 の累乗に広げる（左上に置き、右は透明）
        Assert.Equal(0, dayPng.Rgba[(0 * 16 + 12) * 4 + 3]);
        Assert.Equal((dayPng.W, dayPng.H), (nightPng.W, nightPng.H)); // 昼と夜で同じ大きさ

        var door = r.Sections.First(s => s.Title == "戸");
        Assert.Null(door.Get("NighttimeImage")); // 夜の PSD にない
        Assert.Equal("", door.Get("Subject") ?? "");

        var onlyNight = r.Sections.First(s => s.Title == "夜だけ");
        var transparent = TestPngReader.Read(r.Files.First(f => f.FullPath.EndsWith(Path.Combine("psd", "夜だけ.png"))).Png);
        Assert.All(Enumerable.Range(0, transparent.W * transparent.H), i => Assert.Equal(0, transparent.Rgba[i * 4 + 3]));
        Assert.NotNull(onlyNight.Get("NighttimeImage"));

        var num = r.Sections.First(s => s.Kind == SectionKind.DigitalNumber);
        Assert.Equal("10", num.Get("Interval"));
        Assert.Equal("10, 40", num.Get("Location"));
        var strip = TestPngReader.Read(r.Files.First(f => f.FullPath.EndsWith("ノッチ.png")).Png);
        Assert.Equal((8, 32), (strip.W, strip.H));         // 7×30（3 コマ）を広げる。コマの位置（10px おき）は変わらない
        var baseImg = TestPngReader.Read(r.Files.First(f => f.FullPath.EndsWith(Path.Combine("psd", "base.png"))).Png);
        Assert.Equal((128, 64), (baseImg.W, baseImg.H));
        Assert.Equal(["N0", "N1", "N2"], num.PsdLayers);

        // グループの計器は続けて並ぶ
        var groups = r.Sections.Select(s => s.Group).ToList();
        int first = groups.IndexOf("表示灯"), last = groups.LastIndexOf("表示灯");
        Assert.Equal(groups.Count(g => g == "表示灯") - 1, last - first);
        Assert.Equal(3, groups.Count(g => g == "表示灯")); // 「夜だけ」も夜の PSD では表示灯フォルダの中
    }

    [Fact]
    public void LinkCommentsRoundTrip()
    {
        var (d, n) = WriteDayNight();
        var r = Import(d, n, out var spec);
        using var _1 = spec.Day; using var _2 = spec.Night!;
        var doc = PanelDocument.CreateNew(r.Base!.DayPath, 100, 60);
        doc.This!.Set("NighttimeImage", r.Base.NightPath);
        doc.This.PsdLayers.AddRange(r.Base.Layers);
        doc.PsdDay = "panel_d.psd";
        doc.PsdNight = "panel_n.psd";
        foreach (var s in r.Sections) doc.Sections.Add(s);
        var text = doc.Serialize();
        Assert.Contains(";===== PSD 昼: panel_d.psd =====", text);
        Assert.Contains("[This]\r\n;@PSDレイヤー: 背景 | メーター\r\n", text);
        Assert.Contains("[PilotLamp]\r\n;@PSDレイヤー: 表示灯/ats22 次駅停車\r\n", text);
        Assert.Contains(";===== グループ: 表示灯 =====", text);

        var back = PanelDocument.Parse(text);
        Assert.Equal(text, back.Serialize());
        Assert.Equal("panel_n.psd", back.PsdNight);
        Assert.Equal(r.Sections.Count + 1, back.Sections.Count);
        Assert.Equal("ats22 次駅停車", back.Sections.First(s => s.PsdLayers.Contains("表示灯/ats22 次駅停車")).Title);
        Assert.Empty(back.GroupWarnings);
        var lines = BveText.SplitLines(text);
        var map = back.LineMap();
        for (int i = 0; i < map.Count; i++) Assert.Equal(back.Sections[i].HeaderRaw, lines[map[i].HeaderLine - 1]);
        Assert.True(back.HasPsdLink);
    }

    [Fact]
    public void UpdateFindsMovesAndMissingLayers()
    {
        var (d, n) = WriteDayNight();
        var r = Import(d, n, out var spec);
        var doc = PanelDocument.CreateNew(r.Base!.DayPath, 100, 60);
        doc.This!.PsdLayers.AddRange(r.Base.Layers);
        foreach (var s in r.Sections) doc.Sections.Add(s);
        spec.Day.Dispose(); spec.Night!.Dispose();

        // そのままなら位置は変わらない
        using (var day = PsdFile.Open(d))
        using (var night = PsdFile.Open(n))
        {
            var same = PsdUpdate.Plan(doc, _dir, day, night);
            Assert.Empty(same.Moves);
            Assert.Empty(same.Missing);
            Assert.Equal(5, same.Linked);
            Assert.Contains(same.Files, f => f.FullPath.EndsWith(Path.Combine("psd", "ノッチ.png")));
        }

        // 表示灯を右へ 5 動かした PSD
        var (d2, n2) = WriteDayNight(lampX: 15);
        using (var day = PsdFile.Open(d2))
        using (var night = PsdFile.Open(n2))
        {
            var plan = PsdUpdate.Plan(doc, _dir, day, night);
            var lampIdx = doc.Sections.FindIndex(s => s.Title == "ats22 次駅停車");
            Assert.Contains(plan.Moves, m => m.Section == lampIdx && m.X == 13 && m.Y == 9);
            PsdUpdate.ApplyToDocument(doc, plan, moveLocations: true);
            Assert.Equal("13, 9", doc.Sections[lampIdx].Get("Location"));
        }

        // レイヤーの名前が変わると「見つからない」
        doc.Sections.First(s => s.Title == "戸").PsdLayers[0] = "表示灯/戸閉";
        using (var day = PsdFile.Open(d))
        {
            var plan = PsdUpdate.Plan(doc, _dir, day, null);
            Assert.Contains(plan.Missing, m => m.Contains("戸閉"));
        }
    }

    // ================= 精査で見つけた不具合の確認 =================

    [Fact]
    public void DuplicateNamesAreDistinguishedAndNightBaseUsesAll()
    {
        // 夜の PSD に同じ名前のレイヤーが 2 枚（下が赤、上が青）
        var day = WritePsd("dd.psd", 8, 8, TestLayer.Solid("元画像", new PsdRect(0, 0, 8, 8), 200, 200, 200));
        var night = WritePsd("dn.psd", 8, 8,
            TestLayer.Solid("元画像", new PsdRect(0, 0, 8, 8), 255, 0, 0),
            TestLayer.Solid("元画像", new PsdRect(0, 0, 4, 8), 0, 0, 255));
        using var n = PsdFile.Open(night);
        Assert.Equal(["元画像 #2", "元画像"], n.Layers.OrderBy(l => l.Index).Select(l => l.Path)); // 上（手前）が名前そのまま
        Assert.Equal(1, n.Find("元画像")!.Index);       // いちばん上
        Assert.Equal(2, n.FindAllSameName("元画像").Count);
        using var d = PsdFile.Open(day);
        var r = PsdImport.Build(new PsdImportSpec { Day = d, Night = n, PanelDir = _dir, OutputDir = _dir, BaseLayers = ["元画像"] });
        var baseN = TestPngReader.Read(r.Files.First(f => f.FullPath.EndsWith("base_N.png")).Png);
        Assert.Equal((0, 0, 255), (baseN.Rgba[0], baseN.Rgba[1], baseN.Rgba[2]));            // 左は上の青
        int right = (0 * 8 + 6) * 4;
        Assert.Equal((255, 0, 0), (baseN.Rgba[right], baseN.Rgba[right + 1], baseN.Rgba[right + 2])); // 右は下の赤も入る
    }

    [Fact]
    public void ClippedLayerIsCutByItsBase()
    {
        var baseLayer = TestLayer.Solid("土台", new PsdRect(0, 0, 4, 8), 255, 255, 255);
        var clipped = TestLayer.Solid("色", new PsdRect(0, 0, 8, 8), 255, 0, 0);
        clipped.Clipped = true;
        clipped.Hidden = true;
        var p = WritePsd("clip.psd", 8, 8, baseLayer, clipped);
        using var psd = PsdFile.Open(p);
        var img = PsdCompose.RenderLayer(psd, psd.Find("色")!, psd.Canvas);
        Assert.Equal(255, At(img, 1, 1).A);
        Assert.Equal(0, At(img, 6, 1).A); // 土台のない右半分は透明
    }

    [Fact]
    public void DayAndNightFileNamesNeverCollide()
    {
        var day = WritePsd("cd.psd", 20, 10,
            TestLayer.Solid("戸", new PsdRect(0, 0, 4, 4), 255, 0, 0),
            TestLayer.Solid("戸_N", new PsdRect(10, 0, 14, 4), 0, 255, 0));
        var night = WritePsd("cn.psd", 20, 10, TestLayer.Solid("戸", new PsdRect(0, 0, 4, 4), 0, 0, 255));
        using var d = PsdFile.Open(day);
        using var n = PsdFile.Open(night);
        var r = PsdImport.Build(new PsdImportSpec { Day = d, Night = n, PanelDir = _dir, OutputDir = _dir, LampLayers = ["戸", "戸_N"] });
        Assert.Equal(r.Files.Count, r.Files.Select(f => f.FullPath.ToLowerInvariant()).Distinct().Count());
    }

    [Fact]
    public void PngMarkerAndUpdateSkipsReplacedImages()
    {
        var (d, n) = WriteDayNight();
        var r = Import(d, n, out var spec);
        spec.Day.Dispose(); spec.Night!.Dispose();
        foreach (var f in r.Files) { Directory.CreateDirectory(Path.GetDirectoryName(f.FullPath)!); File.WriteAllBytes(f.FullPath, f.Png); }
        Assert.True(PngWriter.IsPanelKunPng(r.Files[0].FullPath));

        var doc = PanelDocument.CreateNew(r.Base!.DayPath, 100, 60);
        foreach (var s in r.Sections) doc.Sections.Add(s);
        var lampIdx = doc.Sections.FindIndex(s => s.Title == "ats22 次駅停車");
        // 表示灯の画像を「手で差し替えた」もの（印のない PNG）にする
        var lampPng = Path.Combine(_dir, doc.Sections[lampIdx].Get("DaytimeImage")!);
        File.WriteAllBytes(lampPng, StripText(File.ReadAllBytes(lampPng)));
        Assert.False(PngWriter.IsPanelKunPng(lampPng));

        var (d2, n2) = WriteDayNight(lampX: 15); // PSD では表示灯が動いている
        using var day = PsdFile.Open(d2);
        using var night = PsdFile.Open(n2);
        var plan = PsdUpdate.Plan(doc, _dir, day, night);
        Assert.Single(plan.Skipped);
        Assert.DoesNotContain(plan.Moves, m => m.Section == lampIdx);
        Assert.DoesNotContain(plan.Files, f => f.FullPath == lampPng);
    }

    /// <summary>PNG から tEXt を取り除く（印のない PNG を作る）。</summary>
    private static byte[] StripText(byte[] png)
    {
        var o = new MemoryStream();
        o.Write(png, 0, 8);
        int pos = 8;
        while (pos < png.Length)
        {
            int len = png[pos] << 24 | png[pos + 1] << 16 | png[pos + 2] << 8 | png[pos + 3];
            var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            if (type != "tEXt") o.Write(png, pos, 12 + len);
            pos += 12 + len;
        }
        return o.ToArray();
    }

    [Fact]
    public void MultiplePsdSourcesRoundTripAndFilter()
    {
        var (d, n) = WriteDayNight();
        var r = Import(d, n, out var spec);
        var doc = PanelDocument.CreateNew(r.Base!.DayPath, 100, 60);
        doc.PsdDay = "panel_d.psd";
        doc.PsdNight = "panel_n.psd";
        var src2 = doc.GetOrAddPsdSource(2);
        src2.Day = "monitor.psd";
        foreach (var s in r.Sections) doc.Sections.Add(s);
        doc.Sections[^1].PsdSource = 2;
        var text = doc.Serialize();
        Assert.Contains(";===== PSD 昼: panel_d.psd =====", text);
        Assert.Contains(";===== PSD 昼 #2: monitor.psd =====", text);
        Assert.Contains(";@PSDレイヤー #2: ", text);
        var back = PanelDocument.Parse(text);
        Assert.Equal(text, back.Serialize());
        Assert.Equal(2, back.Sections[^1].PsdSource);
        Assert.Equal("monitor.psd", back.GetPsdSource(2)!.Day);

        // 1 つ目の PSD の更新では、2 つ目の PSD の計器を触らない
        var plan = PsdUpdate.Plan(back, _dir, spec.Day, spec.Night, 1);
        Assert.Equal(back.Sections.Count(s => s.PsdLayers.Count > 0 && s.PsdSource == 1), plan.Linked);
        spec.Day.Dispose(); spec.Night!.Dispose();
    }

    [Fact]
    public void DuplicatedGroupIsNotLinkedToPsd()
    {
        var (d, n) = WriteDayNight();
        var r = Import(d, n, out var spec);
        spec.Day.Dispose(); spec.Night!.Dispose();
        var doc = PanelDocument.CreateNew(r.Base!.DayPath, 100, 60);
        foreach (var s in r.Sections) doc.Sections.Add(s);
        var copies = doc.DuplicateGroup("表示灯", "表示灯 のコピー", 10, 10);
        Assert.All(copies, i => Assert.Empty(doc.Sections[i].PsdLayers));
    }

    [Fact]
    public void GuessSubjectAndSafeNames()
    {
        Assert.Equal("ats22", PsdImport.GuessSubject("ats22 次駅停車"));
        Assert.Equal("door", PsdImport.GuessSubject("door"));
        Assert.Equal("", PsdImport.GuessSubject("戸閉"));
        Assert.Equal("a_b", PsdImport.SafeFileName("a/b"));
    }
}
