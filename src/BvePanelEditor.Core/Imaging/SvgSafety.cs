using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace BvePanelEditor.Core.Imaging;

/// <summary>
/// SVG を描く前の下ごしらえ。外の資源（http・https・file などの href・src、CSS の url()・image-set()・image()・src()・@import、xml-stylesheet）を
/// 参照していたら取り除く（人から受け取った作業ファイルを開いただけで、外へ取りに行かないように）。文書の中の参照（#id）と data: は残す。
/// CSS は style 属性・style 要素のほか、すべての属性の値で見る（fill="url(...)" など）。CSS のエスケープ（\75 rl( など）で書いたものは、
/// ほどいた形でも確かめる。中に埋め込んだ SVG 画像（data:image/svg+xml）も取り除く。
/// 描く側（SharpVectors）でも外の資源は読まない設定にしてあり、これはその手前の守り。
/// </summary>
public static partial class SvgSafety
{
    [GeneratedRegex(@"@import[^;]*;?", RegexOptions.IgnoreCase)]
    private static partial Regex ImportPattern();

    // 前の空白と引用符は (?>…) でまとめて読む（後戻りして「url( '#g')」の空白の所から外の参照とみなさないように）
    [GeneratedRegex(@"url\((?>\s*['""]?\s*)(?!#|data:)[^)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    // 文字列で画像の場所を書ける CSS の関数（url() と違い、中に引用符の文字列を書く）
    [GeneratedRegex(@"(?<![\w-])(?:-webkit-)?(?:image-set|image|src)\([^)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex StringUrlFunctionPattern();

    /// <summary>CSS のエスケープ（\ と 16 進 1〜6 桁、または \ と 1 文字）をほどく。</summary>
    public static string UnescapeCss(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
            int j = i + 1, n = 0, v = 0;
            while (j < s.Length && n < 6 && Uri.IsHexDigit(s[j])) { v = v * 16 + Uri.FromHex(s[j]); j++; n++; }
            if (n > 0)
            {
                // 16 進のあとの空白 1 つ（\r\n は 1 つと数える）はエスケープの区切り
                if (j < s.Length && s[j] is ' ' or '\t' or '\n' or '\r' or '\f')
                {
                    if (s[j] == '\r' && j + 1 < s.Length && s[j + 1] == '\n') j++;
                    j++;
                }
                sb.Append(v == 0 || v > 0x10FFFF || (v >= 0xD800 && v <= 0xDFFF) ? "�" : char.ConvertFromUtf32(v));
                i = j - 1;
            }
            else if (s[j] is '\n' or '\r' or '\f')
            {
                // 文字列の中の行の続き（文字にはならない）
                if (s[j] == '\r' && j + 1 < s.Length && s[j + 1] == '\n') j++;
                i = j;
            }
            else { sb.Append(s[j]); i = j; }
        }
        return sb.ToString();
    }

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
    /// 中に埋め込んだ SVG（data:image/svg+xml）か。その中の外の参照までは確かめられず、描く側（SharpVectors）も画像として読めずに
    /// SVG 全体が読めなくなるので、取り除く。
    /// </summary>
    public static bool IsEmbeddedSvg(string value) => value.Trim().StartsWith("data:image/svg+xml", StringComparison.OrdinalIgnoreCase);

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
        string Strip(string css)
        {
            string s = ImportPattern().Replace(css, m => { count++; return ""; });
            s = UrlPattern().Replace(s, m => { count++; return "none"; });
            return StringUrlFunctionPattern().Replace(s, m => { count++; return "none"; });
        }
        string Clean(string css)
        {
            string s = Strip(css);
            // エスケープをほどいた形に外の参照が残っていれば、ほどいた形のまま外す（残っていなければ、元の書き方を残す）
            if (s.Contains('\\'))
            {
                string plain = UnescapeCss(s);
                string again = Strip(plain);
                if (again != plain) s = again;
            }
            return s;
        }
        foreach (var el in doc.SelectNodes("//*")!.OfType<XmlElement>().ToList())
        {
            foreach (var attr in el.Attributes.OfType<XmlAttribute>().ToList())
            {
                if (attr.LocalName is "href" or "src")
                {
                    if (IsExternal(attr.Value) || IsEmbeddedSvg(attr.Value)) { el.Attributes.Remove(attr); count++; }
                }
                else if (attr.Prefix != "xmlns" && attr.LocalName != "xmlns")
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
