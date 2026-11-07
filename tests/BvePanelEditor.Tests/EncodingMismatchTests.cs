using System.Text;
using BvePanelEditor.Core;
using Xunit;

namespace BvePanelEditor.Tests;

/// <summary>1 行目の文字コードの指定と中身が違うファイルは、中身の文字コードで読み書きする。</summary>
public sealed class EncodingMismatchTests
{
    private const string Body = "BveTs Instrument Panel 2.01:sjis\r\n\r\n;速度計\r\n[Needle]\r\nSubject = kmph\r\n";

    [Fact]
    public void SjisHeaderButUtf8BodyIsReadAsUtf8()
    {
        var bytes = new UTF8Encoding(false).GetBytes(Body);
        var (text, info) = BveText.Decode(bytes);
        Assert.Equal(Body, text);
        Assert.IsType<UTF8Encoding>(info.Encoding);
        Assert.NotNull(info.HeaderEncoding);
        Assert.Equal("sjis", info.HeaderEncodingName);
        // 保存も UTF-8（ヘッダがそのままなら）。書いたバイトは元と同じ
        var save = BveText.ResolveSaveEncoding(text, info);
        Assert.IsType<UTF8Encoding>(save);
        Assert.Equal(bytes, BveText.Encode(text, save, info.HasBom));
        // ヘッダを utf-8 に直せば、ふつうに UTF-8
        Assert.IsType<UTF8Encoding>(BveText.ResolveSaveEncoding(text.Replace(":sjis", ":utf-8"), info));
        // ヘッダを別の指定に書き換えたら、その指定に従う
        Assert.Equal(1252, BveText.ResolveSaveEncoding(text.Replace(":sjis", ":windows-1252"), info).CodePage);
    }

    [Fact]
    public void Utf8HeaderButSjisBodyIsReadAsSjis()
    {
        var src = Body.Replace(":sjis", ":utf-8");
        var bytes = BveText.ShiftJis.GetBytes(src);
        var (text, info) = BveText.Decode(bytes);
        Assert.Equal(src, text);
        Assert.Equal(932, info.Encoding.CodePage);
        Assert.Equal("utf-8", info.HeaderEncodingName);
        Assert.Equal(bytes, BveText.Encode(text, BveText.ResolveSaveEncoding(text, info), false));
    }

    [Fact]
    public void MatchingHeaderHasNoWarning()
    {
        var (text, info) = BveText.Decode(BveText.ShiftJis.GetBytes(Body));
        Assert.Equal(Body, text);
        Assert.Equal(932, info.Encoding.CodePage);
        Assert.Null(info.HeaderEncoding);
        // ASCII だけなら、どちらで読んでも同じなので食い違いにしない
        var ascii = "BveTs Instrument Panel 2.00:sjis\r\n[This]\r\n";
        var (_, info2) = BveText.Decode(Encoding.ASCII.GetBytes(ascii));
        Assert.Null(info2.HeaderEncoding);
    }
}
