using System.Text;
using BvePanelEditor.Core.Psd;
using Xunit;

namespace BvePanelEditor.Tests;

/// <summary>テスト用の記述子（Descriptor）書き出し。</summary>
public static class TestDesc
{
    public sealed record En(string Type, string Value);
    public sealed record Unit(string U, double V);
    public sealed record Obj(string Class, Dictionary<string, object> Items);

    /// <summary>lfx2 のデータ（効果の版 0、記述子の版 16、記述子）。</summary>
    public static byte[] Lfx2(Dictionary<string, object> items)
    {
        var ms = new MemoryStream();
        U32(ms, 0); U32(ms, 16);
        Descriptor(ms, "null", items);
        return ms.ToArray();
    }

    public static Obj Rgb(double r, double g, double b) => new("RGBC", new() { ["Rd  "] = r, ["Grn "] = g, ["Bl  "] = b });

    private static void U32(Stream s, uint v) => s.Write([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
    private static void Id(Stream s, string k)
    {
        if (k.Length == 4) U32(s, 0); else U32(s, (uint)k.Length);
        s.Write(Encoding.ASCII.GetBytes(k));
    }
    private static void Dbl(Stream s, double d) { var x = BitConverter.GetBytes(d); Array.Reverse(x); s.Write(x); }

    private static void Descriptor(Stream s, string cls, Dictionary<string, object> items)
    {
        U32(s, 1); s.Write([0, 0]);
        Id(s, cls);
        U32(s, (uint)items.Count);
        foreach (var (k, v) in items) { Id(s, k); Value(s, v); }
    }

    private static void Value(Stream s, object v)
    {
        switch (v)
        {
            case bool b: s.Write("bool"u8); s.WriteByte((byte)(b ? 1 : 0)); break;
            case double d: s.Write("doub"u8); Dbl(s, d); break;
            case int i: s.Write("long"u8); U32(s, (uint)i); break;
            case Unit u: s.Write("UntF"u8); s.Write(Encoding.ASCII.GetBytes(u.U)); Dbl(s, u.V); break;
            case En e: s.Write("enum"u8); Id(s, e.Type); Id(s, e.Value); break;
            case Obj o: s.Write("Objc"u8); Descriptor(s, o.Class, o.Items); break;
            case List<object> list:
                s.Write("VlLs"u8); U32(s, (uint)list.Count);
                foreach (var x in list) Value(s, x);
                break;
            default: throw new ArgumentException(v.GetType().Name);
        }
    }
}

/// <summary>レイヤー効果。</summary>
public sealed class PsdEffectTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "panelkun_fx_" + Guid.NewGuid().ToString("N"));
    public PsdEffectTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    static readonly PsdRect Square = new(6, 6, 14, 14); // 20×20 のキャンバスの中央 8×8

    private static TestDesc.Unit Px(double v) => new("#Pxl", v);
    private static TestDesc.Unit Pct(double v) => new("#Prc", v);
    private static TestDesc.Unit Ang(double v) => new("#Ang", v);
    private static TestDesc.En Mode(string m) => new("BlnM", m);

    private PsdFile Open(Dictionary<string, object> fx, byte fill = 255)
    {
        var p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".psd");
        var sq = TestLayer.Solid("四角", Square, 255, 255, 255);
        sq.Blocks = [("lfx2", TestDesc.Lfx2(fx))];
        if (fill != 255) sq.Blocks.Add(("iOpa", [fill, 0, 0, 0]));
        TestPsdWriter.Write(p, 20, 20, [sq]);
        return PsdFile.Open(p);
    }

    private static (int R, int G, int B, int A) At(PsdPixels px, int x, int y)
    {
        int i = ((y - px.Bounds.Top) * px.Width + (x - px.Bounds.Left)) * 4;
        return (px.Bgra[i + 2], px.Bgra[i + 1], px.Bgra[i], px.Bgra[i + 3]);
    }

    private static PsdPixels All(PsdFile psd) => PsdCompose.Compose(psd, psd.Layers, psd.Canvas);

    private static Dictionary<string, object> Fx(string key, Dictionary<string, object> e) =>
        new() { ["Scl "] = Pct(100), ["masterFXSwitch"] = true, [key] = new TestDesc.Obj(key, e) };

    [Fact]
    public void DropShadowBelowLayer()
    {
        using var psd = Open(Fx("DrSh", new()
        {
            ["enab"] = true, ["Md  "] = Mode("Nrml"), ["Clr "] = TestDesc.Rgb(0, 0, 255), ["Opct"] = Pct(100),
            ["uglg"] = false, ["lagl"] = Ang(90), ["Dstn"] = Px(3), ["Ckmt"] = Pct(0), ["blur"] = Px(0),
        }));
        var l = psd.Find("四角")!;
        var e = Assert.Single(l.Effects!.Items);
        Assert.Equal(PsdEffectKind.DropShadow, e.Kind);
        Assert.Equal(3, e.Distance);
        var img = All(psd);
        Assert.Equal((255, 255, 255, 255), At(img, 8, 8));   // 本体が上
        Assert.Equal((0, 0, 255, 255), At(img, 8, 15));      // 光が上（90°）なので影は下に 3px
        Assert.Equal(0, At(img, 8, 4).A);
        Assert.True(PsdEffects.VisualBounds(l).Bottom >= 17);
    }

    [Fact]
    public void ColorOverlayIgnoresFillOpacity()
    {
        using var psd = Open(Fx("SoFi", new() { ["enab"] = true, ["Md  "] = Mode("Nrml"), ["Clr "] = TestDesc.Rgb(255, 0, 0), ["Opct"] = Pct(100) }), fill: 0);
        var img = All(psd);
        Assert.Equal((255, 0, 0, 255), At(img, 8, 8));   // 塗り 0% でもオーバーレイは見える
        Assert.Equal(0, At(img, 2, 2).A);
    }

    [Theory]
    [InlineData("OutF", 4, 9, true)]   // 外側: 四角の左 2px の所に線
    [InlineData("OutF", 7, 9, false)]  // 外側: 中は白のまま
    [InlineData("InsF", 7, 9, true)]   // 内側: 中の縁に線
    [InlineData("InsF", 4, 9, false)]
    public void Stroke(string position, int x, int y, bool stroked)
    {
        using var psd = Open(Fx("FrFX", new()
        {
            ["enab"] = true, ["Styl"] = new TestDesc.En("FStl", position), ["PntT"] = new TestDesc.En("FrFl", "SClr"),
            ["Md  "] = Mode("Nrml"), ["Opct"] = Pct(100), ["Sz  "] = Px(3), ["Clr "] = TestDesc.Rgb(0, 255, 0),
        }));
        var c = At(All(psd), x, y);
        if (stroked) Assert.Equal((0, 255, 0, 255), c);
        else Assert.NotEqual((0, 255, 0, 255), c);
    }

    [Fact]
    public void GradientOverlayLeftToRight()
    {
        var grad = new TestDesc.Obj("Grdn", new()
        {
            ["GrdF"] = new TestDesc.En("GrdF", "CstS"),
            ["Clrs"] = new List<object>
            {
                new TestDesc.Obj("Clrt", new() { ["Clr "] = TestDesc.Rgb(0, 0, 0), ["Type"] = new TestDesc.En("Clry", "UsrS"), ["Lctn"] = 0, ["Mdpn"] = 50 }),
                new TestDesc.Obj("Clrt", new() { ["Clr "] = TestDesc.Rgb(255, 255, 255), ["Type"] = new TestDesc.En("Clry", "UsrS"), ["Lctn"] = 4096, ["Mdpn"] = 50 }),
            },
            ["Trns"] = new List<object>
            {
                new TestDesc.Obj("TrnS", new() { ["Opct"] = Pct(100), ["Lctn"] = 0, ["Mdpn"] = 50 }),
                new TestDesc.Obj("TrnS", new() { ["Opct"] = Pct(100), ["Lctn"] = 4096, ["Mdpn"] = 50 }),
            },
        });
        using var psd = Open(Fx("GrFl", new()
        {
            ["enab"] = true, ["Md  "] = Mode("Nrml"), ["Opct"] = Pct(100), ["Grad"] = grad, ["Angl"] = Ang(0),
            ["Type"] = new TestDesc.En("GrdT", "Lnr "), ["Rvrs"] = false, ["Algn"] = true, ["Scl "] = Pct(100),
        }));
        var img = All(psd);
        var left = At(img, 6, 9).R;
        var right = At(img, 13, 9).R;
        Assert.True(left < 40 && right > 215, $"{left} {right}");
    }

    [Fact]
    public void InnerGlowEdgeAndOuterGlow()
    {
        var fx = new Dictionary<string, object>
        {
            ["Scl "] = Pct(100),
            ["IrGl"] = new TestDesc.Obj("IrGl", new() { ["enab"] = true, ["Md  "] = Mode("Nrml"), ["Clr "] = TestDesc.Rgb(255, 0, 0), ["Opct"] = Pct(100), ["Ckmt"] = Pct(0), ["blur"] = Px(3), ["glwS"] = new TestDesc.En("IGSr", "SrcE") }),
            ["OrGl"] = new TestDesc.Obj("OrGl", new() { ["enab"] = true, ["Md  "] = Mode("Nrml"), ["Clr "] = TestDesc.Rgb(0, 0, 255), ["Opct"] = Pct(100), ["Ckmt"] = Pct(0), ["blur"] = Px(3) }),
        };
        using var psd = Open(fx);
        var img = All(psd);
        var edge = At(img, 6, 10);
        var center = At(img, 10, 10);
        Assert.True(edge.R > edge.G + 40, $"{edge}");          // 縁は赤っぽい
        Assert.Equal((255, 255, 255, 255), center);            // 中央は白のまま
        var outside = At(img, 5, 10);
        Assert.True(outside.B > 100 && outside.A > 50, $"{outside}"); // 外は青い光彩
    }

    [Fact]
    public void BevelSatinAndInnerShadowDrawInside()
    {
        var fx = new Dictionary<string, object>
        {
            ["Scl "] = Pct(100),
            ["ebbl"] = new TestDesc.Obj("ebbl", new()
            {
                ["enab"] = true, ["hglM"] = Mode("Scrn"), ["hglC"] = TestDesc.Rgb(255, 255, 255), ["hglO"] = Pct(100),
                ["sdwM"] = Mode("Nrml"), ["sdwC"] = TestDesc.Rgb(0, 0, 0), ["sdwO"] = Pct(100), ["bvlS"] = new TestDesc.En("BESl", "InrB"),
                ["uglg"] = false, ["lagl"] = Ang(135), ["Lald"] = Ang(30), ["srgR"] = Pct(100), ["blur"] = Px(3), ["bvlD"] = new TestDesc.En("BESs", "In  "),
            }),
            ["ChFX"] = new TestDesc.Obj("ChFX", new() { ["enab"] = true, ["Md  "] = Mode("Mltp"), ["Clr "] = TestDesc.Rgb(0, 0, 0), ["Opct"] = Pct(50), ["lagl"] = Ang(19), ["Dstn"] = Px(2), ["blur"] = Px(2), ["Invr"] = true }),
            ["IrSh"] = new TestDesc.Obj("IrSh", new() { ["enab"] = true, ["Md  "] = Mode("Mltp"), ["Clr "] = TestDesc.Rgb(0, 0, 0), ["Opct"] = Pct(75), ["uglg"] = false, ["lagl"] = Ang(90), ["Dstn"] = Px(2), ["Ckmt"] = Pct(0), ["blur"] = Px(2) }),
        };
        using var psd = Open(fx);
        var l = psd.Find("四角")!;
        Assert.Equal(3, l.Effects!.Items.Count);
        Assert.Empty(l.Effects.Unsupported);
        Assert.Contains(l.Effects.Approximate, a => a.Contains("ベベル"));
        var img = All(psd);
        // 光は右上（135°）から当たらない左上… 下の縁（光と反対）は影で暗い
        var bottomEdge = At(img, 10, 13);
        Assert.True(bottomEdge.R < 200, $"{bottomEdge}");
        Assert.Equal(0, At(img, 2, 2).A); // 外には何も出ない
        Assert.Contains("効果: ", PsdImport.Notes(l));
    }

    [Fact]
    public void PatternOverlayIsReportedAndMasterSwitchOff()
    {
        using var psd = Open(Fx("patternFill", new() { ["enab"] = true, ["Md  "] = Mode("Nrml"), ["Opct"] = Pct(100) }));
        var l = psd.Find("四角")!;
        Assert.Contains("パターンオーバーレイ", l.Effects!.Unsupported);
        Assert.Contains(PsdEffects.Warnings(l), w => w.Contains("再現しません"));

        var off = Fx("SoFi", new() { ["enab"] = true, ["Md  "] = Mode("Nrml"), ["Clr "] = TestDesc.Rgb(255, 0, 0), ["Opct"] = Pct(100) });
        off["masterFXSwitch"] = false;
        using var psd2 = Open(off);
        Assert.Empty(psd2.Find("四角")!.Effects!.Items);
        Assert.Equal((255, 255, 255, 255), At(All(psd2), 8, 8));
    }

    [Fact]
    public void LampCutoutIncludesShadow()
    {
        var p = Path.Combine(_dir, "lamp.psd");
        var sq = TestLayer.Solid("灯", Square, 255, 255, 255);
        sq.Blocks = [("lfx2", TestDesc.Lfx2(Fx("DrSh", new()
        {
            ["enab"] = true, ["Md  "] = Mode("Nrml"), ["Clr "] = TestDesc.Rgb(0, 0, 0), ["Opct"] = Pct(100),
            ["uglg"] = false, ["lagl"] = Ang(90), ["Dstn"] = Px(3), ["Ckmt"] = Pct(0), ["blur"] = Px(0),
        })))];
        TestPsdWriter.Write(p, 20, 20, [sq]);
        using var psd = PsdFile.Open(p);
        var w = new List<string>();
        var lamp = PsdImport.RenderLamp(psd, null, "灯", w)!;
        Assert.True(lamp.Rect.Bottom >= 17);
        Assert.Equal(255, At(lamp.Day, 8, 16).A);   // 影も切り出しに入る
    }
}
