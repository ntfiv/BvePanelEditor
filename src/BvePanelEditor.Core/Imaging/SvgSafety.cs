using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace BvePanelEditor.Core.Imaging;

/// <summary>
/// SVG を描く前の下ごしらえ。外の資源（http・https・file などの href、CSS の url()・@import、xml-stylesheet）を参照していたら取り除く
/// （人から受け取った作業ファイルを開いただけで、外へ取りに行かないように）。文書の中の参照（#id）と data: は残す。
/// </summary>
public static partial class SvgSafety
{
    [GeneratedRegex(@"@import[^;]*;?", RegexOptions.IgnoreCase)]
    private static partial Regex ImportPattern();

    [GeneratedRegex(@"url\(\s*(['""]?)\s*(?!#|data:)[^)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    /// <summary>中身が SVG らしいか（先頭の 4KB に &lt;svg がある）。拡張子に頼らず判定する。</summary>
    public static bool LooksLikeSvg(byte[] bytes)
    {
        int n = Math.Min(bytes.Length, 4096);
        string head = Encoding.UTF8.GetString(bytes, 0, n).TrimStart('﻿', ' ', '\t', '\r', '\n');
        return head.StartsWith('<') && head.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>外を指す参照か（空・#id・data: 以外）。</summary>
    public static bool IsExternal(string value)
    {
        string v = value.Trim();
        return v.Length > 0 && !v.StartsWith('#') && !v.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 外の資源への参照を取り除いた SVG を返す（removed は取り除いた数。0 なら元の bytes をそのまま返す）。
    /// XML として読めなければ InvalidDataException。DTD は読まず、外の実体も解決しない。
    /// </summary>
    public static byte[] StripExternal(byte[] bytes, out int removed)
    {
        removed = 0;
        var doc = new XmlDocument { XmlResolver = null, PreserveWhitespace = true };
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using var xr = XmlReader.Create(new MemoryStream(bytes), settings);
            doc.Load(xr);
        }
        catch (XmlException ex) { throw new InvalidDataException("SVG を XML として読めませんでした: " + ex.Message, ex); }

        int count = 0;
        // xml-stylesheet などの処理命令（外の CSS を読み込める）
        foreach (var pi in doc.SelectNodes("//processing-instruction()")!.OfType<XmlProcessingInstruction>().ToList())
        {
            pi.ParentNode?.RemoveChild(pi); count++;
        }
        string Clean(string css)
        {
            string s = ImportPattern().Replace(css, m => { count++; return ""; });
            return UrlPattern().Replace(s, m => { count++; return "none"; });
        }
        foreach (var el in doc.SelectNodes("//*")!.OfType<XmlElement>().ToList())
        {
            foreach (var attr in el.Attributes.OfType<XmlAttribute>().ToList())
            {
                if (attr.LocalName is "href" or "src")
                {
                    if (IsExternal(attr.Value)) { el.Attributes.Remove(attr); count++; }
                }
                else if (attr.Value.Contains("url(", StringComparison.OrdinalIgnoreCase) || attr.Value.Contains("@import", StringComparison.OrdinalIgnoreCase))
                {
                    string v = Clean(attr.Value);
                    if (v != attr.Value) attr.Value = v;
                }
            }
            if (el.LocalName == "style")
            {
                string css = el.InnerText;
                string v = Clean(css);
                if (v != css) el.InnerText = v;
            }
        }
        removed = count;
        if (count == 0) return bytes;
        using var ms = new MemoryStream();
        using (var xw = XmlWriter.Create(ms, new XmlWriterSettings { Encoding = new UTF8Encoding(false) })) doc.Save(xw);
        return ms.ToArray();
    }
}
