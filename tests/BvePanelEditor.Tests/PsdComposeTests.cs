using System.Text;
using BvePanelEditor.Core.Psd;
using Xunit;

namespace BvePanelEditor.Tests;

/// <summary>フォルダのマスク・描画モード・フォルダへのクリッピング・べた塗り・調整レイヤー。</summary>
public sealed class PsdComposeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "panelkun_compose_" + Guid.NewGuid().ToString("N"));
    public PsdComposeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    static readonly PsdRect Full = new(0, 0, 8, 4);

    private PsdFile Open(params TestLayer[] layers)
    {
        var p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".psd");
        TestPsdWriter.Write(p, 8, 4, layers);
        return PsdFile.Open(p);
    }

    private static (int R, int G, int B, int A) At(PsdPixels px, int x, int y)
    {
        int i = ((y - px.Bounds.Top) * px.Width + (x - px.Bounds.Left)) * 4;
        return (px.Bgra[i + 2], px.Bgra[i + 1], px.Bgra[i], px.Bgra[i + 3]);
    }

    private static PsdPixels All(PsdFile psd) => PsdCompose.Compose(psd, psd.Layers, psd.Canvas);

    /// <summary>左半分が 255、右半分が 0 のマスク。</summary>
    private static byte[] LeftHalf()
    {
        var m = new byte[8 * 4];
        for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) m[y * 8 + x] = 255;
        return m;
    }

    private static TestLayer Adjustment(string name, string key, byte[] data) =>
        new() { Name = name, Bounds = default, Bgra = [], Blocks = [(key, data)] };

    private static byte[] Be(params int[] u16) => u16.SelectMany(v => new[] { (byte)(v >> 8), (byte)v }).ToArray();

    [Fact]
    public void FolderMaskLimitsChildren()
    {
        using var psd = Open(
            TestLayer.Solid("板", Full, 100, 100, 100),
            TestLayer.FolderEnd(),
            TestLayer.Solid("赤", Full, 255, 0, 0),
            new TestLayer { Name = "夜", SectionType = 1, MaskBounds = Full, Mask = LeftHalf() });
        Assert.NotNull(psd.GetMask(psd.Find("夜")!));
        var img = All(psd);
        Assert.Equal((255, 0, 0, 255), At(img, 1, 1));
        Assert.Equal((100, 100, 100, 255), At(img, 6, 1));
    }

    [Theory]
    [InlineData("diff", 128, 200, 72)]
    [InlineData("fsub", 128, 200, 0)]
    [InlineData("fsub", 200, 128, 72)]
    [InlineData("smud", 0, 200, 200)]
    [InlineData("lbrn", 200, 100, 45)]
    [InlineData("idiv", 255, 10, 255)]
    [InlineData("pLit", 200, 50, 100)]
    [InlineData("hMix", 200, 100, 255)]
    [InlineData("fdiv", 100, 200, 128)]
    public void SeparableBlendModes(string mode, int bottom, int top, int expected)
    {
        using var psd = Open(
            TestLayer.Solid("下", Full, (byte)bottom, (byte)bottom, (byte)bottom),
            WithBlend(TestLayer.Solid("上", Full, (byte)top, (byte)top, (byte)top), mode));
        Assert.True(PsdCompose.IsSupportedBlend(mode));
        var (r, _, _, _) = At(All(psd), 2, 2);
        Assert.InRange(r, expected - 1, expected + 1);
    }

    private static TestLayer WithBlend(TestLayer l, string mode) { l.Blend = mode; return l; }

    [Fact]
    public void LuminosityAndColorModes()
    {
        using var psd = Open(
            TestLayer.Solid("赤", Full, 255, 0, 0),
            WithBlend(TestLayer.Solid("灰", Full, 128, 128, 128), "lum "));
        var (r, g, b, _) = At(All(psd), 0, 0);
        Assert.Equal(255, r);
        Assert.InRange(g, 70, 76);
        Assert.Equal(g, b);

        using var sat = Open(TestLayer.Solid("赤", Full, 255, 0, 0), WithBlend(TestLayer.Solid("灰", Full, 128, 128, 128), "sat "));
        var s = At(All(sat), 0, 0);
        Assert.Equal(s.R, s.G);  // 彩度 0 の色で「彩度」→ 灰色
    }

    [Fact]
    public void ClipToFolder()
    {
        var small = new PsdRect(2, 1, 4, 3);
        using var psd = Open(
            TestLayer.FolderEnd(),
            TestLayer.Solid("四角", small, 255, 255, 255),
            TestLayer.FolderStart("形"),
            WithClip(TestLayer.Solid("青", Full, 0, 0, 255)));
        var img = All(psd);
        Assert.Equal((0, 0, 255, 255), At(img, 2, 1));
        Assert.Equal(0, At(img, 6, 1).A);
    }

    private static TestLayer WithClip(TestLayer l) { l.Clipped = true; return l; }

    private static byte[] SolidColorBlock(double r, double g, double b)
    {
        var ms = new MemoryStream();
        void U32(uint v) { ms.Write([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]); }
        void Key(string k) { U32(0); ms.Write(Encoding.ASCII.GetBytes(k)); }
        void Dbl(double d) { var x = BitConverter.GetBytes(d); Array.Reverse(x); ms.Write(x); }
        U32(16);                        // 版
        U32(1); ms.Write([0, 0]);       // 名前（空）
        Key("null"); U32(1);
        Key("Clr "); ms.Write(Encoding.ASCII.GetBytes("Objc"));
        U32(1); ms.Write([0, 0]); Key("RGBC"); U32(3);
        Key("Rd  "); ms.Write(Encoding.ASCII.GetBytes("doub")); Dbl(r);
        Key("Grn "); ms.Write(Encoding.ASCII.GetBytes("doub")); Dbl(g);
        Key("Bl  "); ms.Write(Encoding.ASCII.GetBytes("doub")); Dbl(b);
        return ms.ToArray();
    }

    [Fact]
    public void SolidFillWithMask()
    {
        using var psd = Open(
            TestLayer.Solid("板", Full, 0, 0, 0),
            new TestLayer { Name = "べた塗り", Bgra = [], Blocks = [("SoCo", SolidColorBlock(10, 20, 30))], MaskBounds = Full, Mask = LeftHalf() });
        var fill = psd.Find("べた塗り")!;
        Assert.Equal(PsdLayerKind.Fill, fill.Kind);
        Assert.Equal(((byte)10, (byte)20, (byte)30), fill.SolidColor);
        Assert.True(fill.CanCompose);
        var img = All(psd);
        Assert.Equal((10, 20, 30, 255), At(img, 1, 1));
        Assert.Equal((0, 0, 0, 255), At(img, 6, 1));
    }

    [Fact]
    public void LevelsInvertWithMask()
    {
        var levl = new List<int> { 2, 0, 255, 255, 0, 100 };
        for (int k = 1; k < 29; k++) levl.AddRange([0, 255, 0, 255, 100]);
        using var psd = Open(
            TestLayer.Solid("板", Full, 50, 50, 50),
            new TestLayer { Name = "レベル", Bgra = [], Blocks = [("levl", Be([.. levl]))], MaskBounds = Full, Mask = LeftHalf() });
        var l = psd.Find("レベル")!;
        Assert.Equal(PsdLayerKind.Adjustment, l.Kind);
        Assert.Equal("レベル補正", l.Adjustment!.Name);
        Assert.Equal(PsdRole.Base, PsdImport.DefaultRole(l, psd.Canvas));
        var img = All(psd);
        Assert.Equal((205, 205, 205, 255), At(img, 1, 1));
        Assert.Equal((50, 50, 50, 255), At(img, 6, 1));
    }

    [Fact]
    public void CurvesInvert()
    {
        var curv = new byte[] { 0 }.Concat(Be(1, 0, 1, 2, 255, 0, 0, 255)).ToArray();  // 版 1、RGB だけ、点 (入力 0→出力 255)・(255→0)
        using var psd = Open(TestLayer.Solid("板", Full, 50, 100, 200), Adjustment("カーブ", "curv", curv));
        var (r, g, b, _) = At(All(psd), 0, 0);
        Assert.Equal((205, 155, 55), (r, g, b));
    }

    [Fact]
    public void LegacyBrightnessAndOpacity()
    {
        var brit = Be(51, 0, 127).Concat(new byte[] { 0 }).ToArray();
        var adj = Adjustment("明るさ", "brit", brit);
        adj.Opacity = 128;
        using var psd = Open(TestLayer.Solid("板", Full, 100, 100, 100), adj);
        var (r, _, _, _) = At(All(psd), 0, 0);
        Assert.InRange(r, 124, 127); // 100 + 51 × 半分
    }

    [Fact]
    public void HueSaturationDesaturates()
    {
        var hue = new byte[100];
        Be(2).CopyTo(hue, 0);
        Be(unchecked((ushort)-100)).CopyTo(hue, 12); // マスターの彩度 -100
        using var psd = Open(TestLayer.Solid("板", Full, 200, 50, 50), Adjustment("色相", "hue2", hue));
        var (r, g, b, _) = At(All(psd), 0, 0);
        Assert.Equal(r, g);
        Assert.Equal(g, b);
        Assert.InRange(r, 120, 130);
    }

    [Fact]
    public void UnsupportedAdjustmentIsNotComposable()
    {
        using var psd = Open(TestLayer.Solid("板", Full, 100, 100, 100), Adjustment("露光量", "expA", new byte[16]));
        var l = psd.Find("露光量")!;
        Assert.Equal(PsdLayerKind.Adjustment, l.Kind);
        Assert.False(l.CanCompose);
        Assert.Contains("露光量", PsdImport.Cautions(l)); // 使えない調整レイヤーは赤い知らせの方
        Assert.DoesNotContain("露光量", PsdImport.Notes(l));
        Assert.Equal((100, 100, 100, 255), At(All(psd), 0, 0));
    }

    /// <summary>記述子（版＋名前＋クラス＋doub の項目）を作る。露光量のテスト用。</summary>
    private static byte[] Descriptor(params (string Key, double Value)[] items)
    {
        var b = new List<byte>();
        void U32(uint v) { b.Add((byte)(v >> 24)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 8)); b.Add((byte)v); }
        void Ascii(string t) => b.AddRange(Encoding.ASCII.GetBytes(t));
        void Doub(double v) { var x = BitConverter.GetBytes(v); if (BitConverter.IsLittleEndian) Array.Reverse(x); b.AddRange(x); }
        U32(1); U32(0); U32(0); Ascii("    "); U32((uint)items.Length);
        foreach (var (k, v) in items) { U32(0); Ascii(k); Ascii("doub"); Doub(v); }
        return [.. b];
    }

    /// <summary>GdFl の記述子（版つき）を作る。色は黒→白の 2 点。</summary>
    private static byte[] GradientBlock(string type = "Lnr ", bool reverse = false, bool alphaRamp = false, string? interp = null)
    {
        var b = new List<byte>();
        void U32(uint v) { b.Add((byte)(v >> 24)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 8)); b.Add((byte)v); }
        void Ascii(string t) => b.AddRange(Encoding.ASCII.GetBytes(t));
        void Doub(double v) { var x = BitConverter.GetBytes(v); if (BitConverter.IsLittleEndian) Array.Reverse(x); b.AddRange(x); }
        void Id(string t) { if (t.Length == 4) { U32(0); Ascii(t); } else { U32((uint)t.Length); Ascii(t); } }
        void Uni(string t) { U32((uint)t.Length); foreach (var ch in t) { b.Add((byte)(ch >> 8)); b.Add((byte)ch); } }
        void Item(string key, string typeName, Action value) { Id(key); Ascii(typeName); value(); }
        void Desc(params Action[] items) { Uni(""); Id("    "); U32((uint)items.Length); foreach (var it in items) it(); }
        Action ColorStop(double r, double g, double bl, int loc) => () =>
        {
            Ascii("Objc");
            Desc(
                () => Item("Clr ", "Objc", () => Desc(
                    () => Item("Rd  ", "doub", () => Doub(r)),
                    () => Item("Grn ", "doub", () => Doub(g)),
                    () => Item("Bl  ", "doub", () => Doub(bl)))),
                () => Item("Lctn", "long", () => U32((uint)loc)),
                () => Item("Mdpn", "long", () => U32(50)));
        };
        Action AlphaStop(double opct, int loc) => () =>
        {
            Ascii("Objc");
            Desc(
                () => Item("Opct", "UntF", () => { Ascii("#Prc"); Doub(opct); }),
                () => Item("Lctn", "long", () => U32((uint)loc)),
                () => Item("Mdpn", "long", () => U32(50)));
        };
        U32(1); // 版（読み手が読み飛ばす）
        var root = new List<Action>
        {
            () => Item("Grad", "Objc", () => Desc(
                () => Item("Nm  ", "TEXT", () => Uni("")),
                () => Item("GrdF", "enum", () => { Id("GrdF"); Id("CstS"); }),
                () => Item("Clrs", "VlLs", () => { U32(2); ColorStop(0, 0, 0, 0)(); ColorStop(255, 255, 255, 4096)(); }),
                () => Item("Trns", "VlLs", () => { U32(2); AlphaStop(100, 0)(); AlphaStop(alphaRamp ? 0 : 100, 4096)(); }))),
            () => Item("Angl", "UntF", () => { Ascii("#Ang"); Doub(0); }),
            () => Item("Type", "enum", () => { Id("GdTy"); Id(type); }),
            () => Item("Scl ", "UntF", () => { Ascii("#Prc"); Doub(100); }),
            () => Item("Rvrs", "bool", () => b.Add((byte)(reverse ? 1 : 0))),
        };
        if (interp != null) root.Add(() => Item("gradientsInterpolationMethod", "enum", () => { Id("gradientInterpolationMethodType"); Id(interp); }));
        Desc([.. root]);
        return [.. b];
    }

    private static TestLayer GradientLayer(byte[] block) =>
        new() { Name = "グラデーション", Bgra = [], Blocks = [("GdFl", block)] };

    [Fact]
    public void LinearGradientFill()
    {
        using var psd = Open(GradientLayer(GradientBlock()));
        var px = All(psd);
        Assert.InRange(At(px, 0, 0).R, 8, 24);   // 左端は黒に近い（位置 0.0625）
        Assert.InRange(At(px, 3, 0).R, 100, 124); // 中央寄り
        Assert.InRange(At(px, 7, 0).R, 231, 247); // 右端は白に近い（位置 0.9375）
    }

    [Fact]
    public void GradientFillReverse()
    {
        using var psd = Open(GradientLayer(GradientBlock(reverse: true)));
        var px = All(psd);
        Assert.InRange(At(px, 0, 0).R, 231, 247);
        Assert.InRange(At(px, 7, 0).R, 8, 24);
    }

    [Fact]
    public void GradientFillAlphaRamp()
    {
        using var psd = Open(GradientLayer(GradientBlock(alphaRamp: true)));
        var px = All(psd);
        Assert.InRange(At(px, 0, 0).A, 225, 250); // 左はほぼ不透明
        Assert.InRange(At(px, 7, 0).A, 5, 30);    // 右はほぼ透明
    }

    [Fact]
    public void SmoothGradientColor()
    {
        // スムーズ: 区間の混ざり具合が 2 次の曲線（f → 2f−f²）になる。実ファイルの実測に合わせた
        using var psd = Open(GradientLayer(GradientBlock(interp: "Smoo")));
        var px = All(psd);
        Assert.InRange(At(px, 1, 0).R, 78, 96);   // f=0.1875 → 0.34（直線なら 48、クラシックなら 36）
        Assert.InRange(At(px, 3, 0).R, 165, 183); // f=0.4375 → 0.68
    }

    [Fact]
    public void SmoothGradientAlpha()
    {
        using var psd = Open(GradientLayer(GradientBlock(alphaRamp: true, interp: "Smoo")));
        var px = All(psd);
        Assert.InRange(At(px, 3, 0).A, 70, 92); // 不透明度も同じ曲線（1 − 0.68 = 0.32）
    }

    [Fact]
    public void LinearSpaceGradientColor()
    {
        // リニア: 線形の色空間で 3 次補間するので、直線で混ぜるより暗めに出る
        using var psd = Open(GradientLayer(GradientBlock(interp: "linear")));
        var px = All(psd);
        Assert.InRange(At(px, 1, 0).R, 97, 112); // f=0.1875 → 線形で 0.140 → 符号化で 0.41
    }

    [Fact]
    public void PerceptualGradientColor()
    {
        // 知覚的: OKLab で 3 次補間する。灰色の ramp では明度が曲線になる
        using var psd = Open(GradientLayer(GradientBlock(interp: "perceptual")));
        var px = All(psd);
        Assert.InRange(At(px, 3, 0).R, 72, 92); // f=0.4375 → 明度 0.422 → 線形で 0.075 → 符号化で 0.30
    }

    [Fact]
    public void LinearGradientAlpha()
    {
        // リニアの不透明度は、古典の 3 次曲線 H の H^(2/3)（8300_Panel の実ファイルでの実測に合わせた）
        using var psd = Open(GradientLayer(GradientBlock(alphaRamp: true, interp: "linear")));
        var px = All(psd);
        Assert.InRange(At(px, 1, 0).A, 222, 240); // f=0.1875 → H=0.860 → 0.904
        Assert.InRange(At(px, 3, 0).A, 168, 186); // f=0.4375 → H=0.578 → 0.694
    }

    [Fact]
    public void PerceptualGradientAlpha()
    {
        // 知覚的の不透明度は、古典の 3 次曲線 H の H^2（同上）
        using var psd = Open(GradientLayer(GradientBlock(alphaRamp: true, interp: "perceptual")));
        var px = All(psd);
        Assert.InRange(At(px, 1, 0).A, 180, 197); // f=0.1875 → H=0.860 → 0.740
        Assert.InRange(At(px, 3, 0).A, 77, 93);   // f=0.4375 → H=0.578 → 0.334
    }

    [Fact]
    public void ClassicGradientColor()
    {
        // 補間方法の指定が無い（古いファイル）ときはクラシック（3 次補間）
        using var psd = Open(GradientLayer(GradientBlock()));
        var px = All(psd);
        Assert.InRange(At(px, 1, 0).R, 30, 42); // f=0.1875 → 0.14（直線なら 48）
    }

    [Fact]
    public void RadialGradientFill()
    {
        using var psd = Open(GradientLayer(GradientBlock(type: "Rdl ")));
        var px = All(psd);
        Assert.True(At(px, 3, 1).R < 80, "中心は黒に近い");   // 中心から始まるので黒
        Assert.True(At(px, 0, 0).R > 180, "隅は白に近い");
    }

    [Fact]
    public void InvertAdjustment()
    {
        using var psd = Open(TestLayer.Solid("板", Full, 50, 100, 200), Adjustment("反転", "nvrt", []));
        var (r, g, b, _) = At(All(psd), 0, 0);
        Assert.Equal((205, 155, 55), (r, g, b));
    }

    [Fact]
    public void ThresholdAdjustment()
    {
        using var psd = Open(TestLayer.Solid("板", Full, 50, 100, 200), Adjustment("2階調", "thrs", Be(128)));
        var (r, g, b, _) = At(All(psd), 0, 0);
        Assert.Equal((0, 0, 0), (r, g, b)); // 輝度 0.3×50＋0.59×100＋0.11×200＝96 ＜ 128
        using var psd2 = Open(TestLayer.Solid("板", Full, 200, 200, 200), Adjustment("2階調", "thrs", Be(128)));
        var (r2, g2, b2, _) = At(All(psd2), 0, 0);
        Assert.Equal((255, 255, 255), (r2, g2, b2));
    }

    [Fact]
    public void PosterizeAdjustment()
    {
        using var psd = Open(TestLayer.Solid("板", Full, 50, 100, 200), Adjustment("ポスタ", "post", Be(2)));
        var (r, g, b, _) = At(All(psd), 0, 0);
        Assert.Equal((0, 0, 255), (r, g, b));
    }

    [Fact]
    public void ExposureAdjustment()
    {
        var expa = Descriptor(("Exps", 1), ("Ofst", 0), ("Gmmo", 1));
        using var psd = Open(TestLayer.Solid("板", Full, 50, 100, 200), Adjustment("露光量", "expA", expa));
        var l = psd.Find("露光量")!;
        Assert.True(l.CanCompose);
        Assert.Equal("露光量", l.Adjustment!.Name);
        var (r, g, b, _) = At(All(psd), 0, 0);
        Assert.Equal((100, 200, 255), (r, g, b)); // 露光量 +1 で 2 倍（255 で止まる）
    }

    [Fact]
    public void FolderBlendModeWarns()
    {
        // フォルダの描画モード（乗算）は再現できないので警告する
        var mul = TestLayer.FolderStart("乗算グループ"); mul.Blend = "mul ";
        using var psd = Open(
            TestLayer.Solid("板", Full, 200, 200, 200),
            TestLayer.FolderEnd(),
            TestLayer.Solid("赤", Full, 100, 100, 100),
            mul);
        var w = new List<string>();
        PsdCompose.Compose(psd, psd.Layers, psd.Canvas, 1, w);
        Assert.Contains(w, x => x.Contains("乗算グループ") && x.Contains("乗算"));

        // 「通過」なら警告しない。「通常」でも、中身が全部通常なら警告しない
        var pass = TestLayer.FolderStart("通過グループ"); pass.Blend = "pass";
        using var psd2 = Open(
            TestLayer.Solid("板", Full, 200, 200, 200),
            TestLayer.FolderEnd(),
            TestLayer.Solid("赤", Full, 100, 100, 100),
            pass);
        var w2 = new List<string>();
        PsdCompose.Compose(psd2, psd2.Layers, psd2.Canvas, 1, w2);
        Assert.DoesNotContain(w2, x => x.Contains("通過グループ"));

        // 「通常」のフォルダでも、中身に乗算があれば見た目が変わるので警告する
        var norm = TestLayer.FolderStart("通常グループ");
        var child = TestLayer.Solid("赤", Full, 100, 100, 100); child.Blend = "mul ";
        using var psd3 = Open(
            TestLayer.Solid("板", Full, 200, 200, 200),
            TestLayer.FolderEnd(),
            child,
            norm);
        var w3 = new List<string>();
        PsdCompose.Compose(psd3, psd3.Layers, psd3.Canvas, 1, w3);
        Assert.Contains(w3, x => x.Contains("通常グループ"));
    }

    [Fact]
    public void SplineThroughPoints()
    {
        var lut = PsdAdjust.Spline([(0, 0), (128, 200), (255, 255)]);
        Assert.InRange(lut[128] * 255, 199, 201);
        Assert.Equal(0, lut[0]);
        Assert.Equal(1, lut[255]);
    }
}
