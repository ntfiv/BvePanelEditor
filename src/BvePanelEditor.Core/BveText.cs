using System.Text;

namespace BvePanelEditor.Core;

/// <summary>読み込んだテキストファイルの書式（保存時に同じ形で書き戻すため）。</summary>
public sealed class TextFileInfo
{
    public Encoding Encoding { get; init; } = new UTF8Encoding(false);
    public bool HasBom { get; init; }
    public string NewLine { get; init; } = "\r\n";

    /// <summary>ヘッダに文字コード指定がなく、UTF-8 として読めなかったので Shift_JIS で読んだ。</summary>
    public bool EncodingFallback { get; init; }

    /// <summary>
    /// ヘッダの文字コード指定（"sjis" など）と中身が違ったので、中身の文字コードで読んだときの、ヘッダの指定（null なら食い違いなし）。
    /// 保存もヘッダの指定ではなく、中身の文字コード（Encoding）で書く。
    /// </summary>
    public Encoding? HeaderEncoding { get; init; }
    public string? HeaderEncodingName { get; init; }

    public static TextFileInfo NewUtf8() => new();
}

/// <summary>BVE のテキストファイル（1 行目が "BveTs ... :encoding"）の読み書き。</summary>
public static class BveText
{
    static BveText() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static void EnsureInitialized() { }

    public static Encoding ShiftJis => Encoding.GetEncoding(932);

    public static (string Text, TextFileInfo Info) Read(string path) => Decode(File.ReadAllBytes(path));

    public static (string Text, TextFileInfo Info) Decode(byte[] bytes)
    {
        string text;
        Encoding enc;
        bool bom = false, fallback = false;
        Encoding? mismatch = null;
        string? mismatchName = null;

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            enc = new UTF8Encoding(false);
            bom = true;
            text = enc.GetString(bytes, 3, bytes.Length - 3);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            enc = new UnicodeEncoding(false, false);
            bom = true;
            text = enc.GetString(bytes, 2, bytes.Length - 2);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            enc = new UnicodeEncoding(true, false);
            bom = true;
            text = enc.GetString(bytes, 2, bytes.Length - 2);
        }
        else
        {
            int end = 0;
            while (end < bytes.Length && end < 4096 && bytes[end] != '\r' && bytes[end] != '\n') end++;
            string header = Encoding.ASCII.GetString(bytes, 0, end);
            var specified = EncodingFromHeader(header);
            if (specified != null)
            {
                // ヘッダの指定より中身を優先する（":sjis" と書いてあっても中身は UTF-8、のようなファイルがある）
                bool isUtf8 = specified is UTF8Encoding;
                bool hasNonAscii = bytes.Any(b => b >= 0x80);
                bool validUtf8 = TryUtf8(bytes, out var utf8Text);
                if (!isUtf8 && hasNonAscii && validUtf8)
                {
                    enc = new UTF8Encoding(false);
                    text = utf8Text!;
                    mismatch = specified;
                }
                else if (isUtf8 && !validUtf8)
                {
                    enc = ShiftJis;
                    text = enc.GetString(bytes);
                    mismatch = specified;
                }
                else
                {
                    enc = specified;
                    text = enc.GetString(bytes);
                }
                if (mismatch != null) mismatchName = header[(header.IndexOf(':') + 1)..].Trim();
            }
            else
            {
                try
                {
                    text = new UTF8Encoding(false, true).GetString(bytes);
                    enc = new UTF8Encoding(false);
                }
                catch (DecoderFallbackException)
                {
                    enc = ShiftJis;
                    text = enc.GetString(bytes);
                    fallback = true;
                }
            }
        }

        string nl = text.Contains("\r\n") ? "\r\n" : text.Contains('\n') ? "\n" : "\r\n";
        return (text, new TextFileInfo { Encoding = enc, HasBom = bom, NewLine = nl, EncodingFallback = fallback, HeaderEncoding = mismatch, HeaderEncodingName = mismatchName });
    }

    private static bool TryUtf8(byte[] bytes, out string? text)
    {
        try { text = new UTF8Encoding(false, true).GetString(bytes); return true; }
        catch (DecoderFallbackException) { text = null; return false; }
    }

    /// <summary>文字コードの表示名（"UTF-8"・"Shift_JIS" など）。</summary>
    public static string DisplayName(Encoding e) => e switch
    {
        UTF8Encoding => "UTF-8",
        UnicodeEncoding u => u.WebName.Equals("utf-16BE", StringComparison.OrdinalIgnoreCase) ? "UTF-16BE" : "UTF-16",
        _ when e.CodePage == 932 => "Shift_JIS",
        _ => e.WebName,
    };

    /// <summary>"BveTs Instrument Panel 2.00:shift_jis" の ":" 以降から文字コードを得る。</summary>
    public static Encoding? EncodingFromHeader(string header)
    {
        int c = header.IndexOf(':');
        if (c < 0) return null;
        string name = header[(c + 1)..].Trim();
        if (name.Length == 0) return null;
        try
        {
            var e = Encoding.GetEncoding(name);
            return e is UTF8Encoding ? new UTF8Encoding(false) : e;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// 保存に使う文字コード。ヘッダの指定を優先し、なければ読み込み時の文字コード。
    /// ただし、読み込んだときにヘッダの指定と中身が違った（中身の文字コードで読んだ）ファイルは、ヘッダがそのままなら中身の文字コードで書く。
    /// </summary>
    public static Encoding ResolveSaveEncoding(string text, TextFileInfo info)
    {
        // BOM 付きのファイルはヘッダの指定より BOM の文字コードが正（実データに UTF-8 BOM + ":ISO_8859-1" がある）
        if (info.HasBom) return info.Encoding;
        int end = text.IndexOfAny(['\r', '\n']);
        string header = end < 0 ? text : text[..end];
        var specified = EncodingFromHeader(header);
        if (specified != null && info.HeaderEncoding != null && specified.CodePage == info.HeaderEncoding.CodePage) return info.Encoding;
        return specified ?? info.Encoding;
    }

    public static byte[] Encode(string text, Encoding enc, bool bom)
    {
        var body = enc.GetBytes(text);
        if (!bom) return body;
        var pre = enc.GetPreamble();
        if (pre.Length == 0 && enc is UTF8Encoding) pre = [0xEF, 0xBB, 0xBF];
        var all = new byte[pre.Length + body.Length];
        pre.CopyTo(all, 0);
        body.CopyTo(all, pre.Length);
        return all;
    }

    public static string FirstLine(string text)
    {
        int end = text.IndexOfAny(['\r', '\n']);
        return (end < 0 ? text : text[..end]).TrimStart('﻿');
    }

    public static string[] SplitLines(string text) => text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
}
