using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using BvePanelEditor.Core.Imaging;
using BvePanelEditor.Core.Psd;
using SharpVectors.Converters;
using SharpVectors.Dom;
using SharpVectors.Renderers.Wpf;

namespace BvePanelEditor.Rendering;

/// <summary>
/// SVG を読んで画素の絵にする（読むだけ。BVE は SVG を読めないので、パネルには PNG にして置く）。
/// SharpVectors で WPF の絵にしてから描く。外の資源（http・file などの参照）は読まない（モニタくんと同じ）。
/// </summary>
public static partial class SvgImage
{
    /// <summary>描く画像の長い辺の上限。</summary>
    public const int MaxSide = 8192;

    public static bool IsSvg(string path) => Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>読み込んだ SVG（絵と、SVG に書いてある大きさ）。</summary>
    public sealed class Loaded
    {
        public required Drawing Drawing { get; init; }
        /// <summary>絵の座標で見た SVG の表示範囲（これを画像いっぱいに描く）。</summary>
        public required Rect Viewport { get; init; }
        /// <summary>SVG に書いてある大きさ（width・height、なければ viewBox）。画素。</summary>
        public required double Width { get; init; }
        public required double Height { get; init; }
    }

    /// <summary>SVG を読む。読めなければ InvalidDataException。</summary>
    public static Loaded Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (!SvgSafety.LooksLikeSvg(bytes)) throw new InvalidDataException("SVG ではないようです。");
        bytes = SvgSafety.StripExternal(bytes, out _);
        DrawingGroup? dg;
        try
        {
            var settings = new WpfDrawingSettings
            {
                IncludeRuntime = false, TextAsGeometry = true, OptimizePath = true,
                ExternalResourcesAccessMode = ExternalResourcesAccessModes.Ignore,
            };
            using var reader = new FileSvgReader(settings);
            dg = reader.Read(new MemoryStream(bytes));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { throw new InvalidDataException("SVG を読めませんでした: " + ex.Message, ex); }
        if (dg == null) throw new InvalidDataException("SVG を読めませんでした。");
        var (vp, w, h) = Viewport(bytes, dg.Bounds);
        if (vp.Width <= 0 || vp.Height <= 0 || w <= 0 || h <= 0) throw new InvalidDataException("SVG の大きさがわかりませんでした。");
        if (dg.CanFreeze) dg.Freeze();
        return new Loaded { Drawing = dg, Viewport = vp, Width = w, Height = h };
    }

    /// <summary>scale 倍の大きさで描いた画像の大きさ（1 画素以上、長い辺は MaxSide まで）。</summary>
    public static (int W, int H) PixelSize(Loaded svg, double scale)
    {
        double k = scale;
        double longSide = Math.Max(svg.Width, svg.Height) * k;
        if (longSide > MaxSide) k *= MaxSide / longSide;
        return (Math.Max(1, (int)Math.Round(svg.Width * k)), Math.Max(1, (int)Math.Round(svg.Height * k)));
    }

    /// <summary>scale 倍の大きさで描く（SVG に書いてある大きさが 1 倍）。</summary>
    public static PsdPixels Rasterize(Loaded svg, double scale)
    {
        var (pw, ph) = PixelSize(svg, scale);
        var vp = svg.Viewport;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, pw, ph)));
            dc.PushTransform(new MatrixTransform(pw / vp.Width, 0, 0, ph / vp.Height, -vp.X * pw / vp.Width, -vp.Y * ph / vp.Height));
            dc.DrawDrawing(svg.Drawing);
            dc.Pop();
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        // 掛け合わせ済み（Pbgra32）から、パネルの画像と同じ Bgra32 にする
        var conv = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
        var px = new byte[pw * ph * 4];
        conv.CopyPixels(px, pw * 4, 0);
        return new PsdPixels(new PsdRect(0, 0, pw, ph), px);
    }

    // ---- SVG の大きさ（モニタくんと同じ決め方） ----

    private static (Rect Viewport, double Width, double Height) Viewport(byte[] bytes, Rect drawn)
    {
        double? w = null, h = null;
        double[]? vb = null;
        try
        {
            var xs = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true };
            using var xr = XmlReader.Create(new MemoryStream(bytes), xs);
            while (xr.Read())
            {
                if (xr.NodeType != XmlNodeType.Element) continue;
                if (xr.LocalName == "svg")
                {
                    w = Length(xr.GetAttribute("width"));
                    h = Length(xr.GetAttribute("height"));
                    vb = ViewBox(xr.GetAttribute("viewBox"));
                }
                break;
            }
        }
        catch (XmlException) { }
        // SharpVectors は width・height が両方あると、その大きさの座標で描く（viewBox はその中に収める）
        if (w is > 0 && h is > 0) return (new Rect(0, 0, w.Value, h.Value), w.Value, h.Value);
        // 片方だけ・どちらもないときは viewBox の座標で描く。大きさは片方と viewBox の縦横の比から
        if (vb != null)
        {
            double ww = w is > 0 ? w.Value : h is > 0 ? h.Value * vb[2] / vb[3] : vb[2];
            double hh = h is > 0 ? h.Value : w is > 0 ? w.Value * vb[3] / vb[2] : vb[3];
            return (new Rect(vb[0], vb[1], vb[2], vb[3]), ww, hh);
        }
        var r = drawn.IsEmpty || drawn.Width <= 0 || drawn.Height <= 0 ? new Rect(0, 0, 1, 1) : drawn;
        return (r, r.Width, r.Height);
    }

    [GeneratedRegex(@"^\s*([+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)\s*(px|pt|pc|mm|cm|in|em|ex|%)?\s*$")]
    private static partial Regex LengthPattern();

    /// <summary>SVG の長さを画素にする（% は決められないので null）。</summary>
    private static double? Length(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = LengthPattern().Match(s);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return null;
        return m.Groups[2].Value switch
        {
            "" or "px" => v,
            "pt" => v * 96 / 72,
            "pc" => v * 16,
            "mm" => v * 96 / 25.4,
            "cm" => v * 96 / 2.54,
            "in" => v * 96,
            "em" => v * 16,
            "ex" => v * 8,
            _ => null,
        };
    }

    private static double[]? ViewBox(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4) return null;
        var v = new double[4];
        for (int i = 0; i < 4; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return null;
        return v[2] > 0 && v[3] > 0 ? v : null;
    }
}
