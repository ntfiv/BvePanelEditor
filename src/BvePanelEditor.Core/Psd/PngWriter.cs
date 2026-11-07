using System.IO.Compression;

namespace BvePanelEditor.Core.Psd;

/// <summary>RGBA 8bit の PNG を書く（外部ライブラリなし）。</summary>
public static class PngWriter
{
    public static byte[] Encode(PsdPixels px) => Encode(px.Width, px.Height, px.Bgra);

    /// <summary>n 以上で最小の 2 の累乗。</summary>
    public static int NextPow2(int n)
    {
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    /// <summary>
    /// 幅・高さをそれぞれ 2 の累乗に広げて PNG にする（BVE で使える大きさ）。絵は左上に置き、広げた所は透明。
    /// 中身の大きさを PNG に書いておく（切り抜き元から更新で使う。TryReadContentSize）。
    /// </summary>
    public static byte[] EncodePow2(PsdPixels px)
    {
        int w = NextPow2(Math.Max(1, px.Width)), h = NextPow2(Math.Max(1, px.Height));
        var bgra = new byte[w * h * 4];
        for (int y = 0; y < px.Height; y++)
            Buffer.BlockCopy(px.Bgra, y * px.Width * 4, bgra, y * w * 4, px.Width * 4);
        return Encode(w, h, bgra, $"{px.Width}x{px.Height}");
    }

    /// <summary>BGRA（乗算済みでない）から PNG を作る。</summary>
    public static byte[] Encode(int width, int height, byte[] bgra) => Encode(width, height, bgra, null);

    private static byte[] Encode(int width, int height, byte[] bgra, string? content)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        WriteU32(ihdr, 0, (uint)width);
        WriteU32(ihdr, 4, (uint)height);
        ihdr[8] = 8;  // 8bit
        ihdr[9] = 6;  // RGBA
        WriteChunk(ms, "IHDR", ihdr);
        // パネルくんが書き出した印（PSD から更新するとき、手で差し替えた画像を上書きしないため）
        WriteChunk(ms, "tEXt", System.Text.Encoding.ASCII.GetBytes("Software\0" + Marker));
        if (content != null) WriteChunk(ms, "tEXt", System.Text.Encoding.ASCII.GetBytes(ContentKey + "\0" + content));

        // 各行の先頭にフィルタ番号。Sub（1）で左の画素との差を書く
        int stride = width * 4;
        var raw = new byte[(stride + 1) * height];
        for (int y = 0; y < height; y++)
        {
            int o = y * (stride + 1);
            raw[o] = 1;
            int s = y * stride;
            for (int x = 0; x < width; x++)
            {
                int i = s + x * 4;
                byte r = bgra[i + 2], g = bgra[i + 1], b = bgra[i], a = bgra[i + 3];
                if (a == 0) r = g = b = 0; // 透明な所の色は捨てて縮みやすくする
                int p = o + 1 + x * 4;
                if (x == 0)
                {
                    raw[p] = r; raw[p + 1] = g; raw[p + 2] = b; raw[p + 3] = a;
                }
                else
                {
                    int j = i - 4;
                    byte pa = bgra[j + 3];
                    byte pr = pa == 0 ? (byte)0 : bgra[j + 2], pg = pa == 0 ? (byte)0 : bgra[j + 1], pb = pa == 0 ? (byte)0 : bgra[j];
                    raw[p] = (byte)(r - pr); raw[p + 1] = (byte)(g - pg); raw[p + 2] = (byte)(b - pb); raw[p + 3] = (byte)(a - pa);
                }
            }
        }
        using (var zbuf = new MemoryStream())
        {
            using (var z = new ZLibStream(zbuf, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
            WriteChunk(ms, "IDAT", zbuf.ToArray());
        }
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    /// <summary>書き出した PNG に入れる印。</summary>
    public const string Marker = "PanelKun PSD export";

    /// <summary>パネルくんが PSD から書き出した PNG か（印があるか）。読めなければ false。</summary>
    public static bool IsPanelKunPng(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            return HasMarker(f);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }

    /// <summary>PNG のバイト列に印があるか。</summary>
    public static bool IsPanelKunPngBytes(byte[] png)
    {
        using var ms = new MemoryStream(png);
        return HasMarker(ms);
    }

    private static bool HasMarker(Stream f) => Texts(f).Any(t => t.EndsWith(Marker));

    /// <summary>中身の大きさを書いておく tEXt のキー。</summary>
    public const string ContentKey = "PanelKun content";

    /// <summary>画像データより前にある tEXt（「キー\0値」）を全部読む。</summary>
    private static List<string> Texts(Stream f)
    {
        var list = new List<string>();
        var head = new byte[8];
        if (f.Read(head, 0, 8) != 8 || head[0] != 0x89 || head[1] != 0x50) return list;
        var buf = new byte[8];
        while (f.Read(buf, 0, 8) == 8)
        {
            int len = buf[0] << 24 | buf[1] << 16 | buf[2] << 8 | buf[3];
            var type = System.Text.Encoding.ASCII.GetString(buf, 4, 4);
            if (type is "IDAT" or "IEND" || len < 0) break;
            if (type == "tEXt" && len < 4096)
            {
                var data = new byte[len];
                if (f.Read(data, 0, len) != len) break;
                list.Add(System.Text.Encoding.ASCII.GetString(data));
                f.Seek(4, SeekOrigin.Current);
            }
            else f.Seek(len + 4, SeekOrigin.Current);
        }
        return list;
    }

    /// <summary>
    /// 中身の大きさ。2 の累乗に広げて書き出した PNG なら広げる前の大きさ、そうでなければ PNG の大きさ。
    /// </summary>
    public static bool TryReadContentSize(string path, out int width, out int height)
    {
        if (!TryReadSize(path, out width, out height)) return false;
        try
        {
            using var f = File.OpenRead(path);
            foreach (var t in Texts(f))
            {
                if (!t.StartsWith(ContentKey + "\0")) continue;
                var v = t[(ContentKey.Length + 1)..].Split('x');
                if (v.Length == 2 && int.TryParse(v[0], out var w) && int.TryParse(v[1], out var h) && w > 0 && h > 0 && w <= width && h <= height)
                {
                    width = w; height = h;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return true;
    }

    /// <summary>PNG の幅と高さ（IHDR）を読む。</summary>
    public static bool TryReadSize(string path, out int width, out int height)
    {
        width = height = 0;
        try
        {
            using var f = File.OpenRead(path);
            var b = new byte[24];
            if (f.Read(b, 0, 24) != 24 || b[0] != 0x89 || b[12] != (byte)'I' || b[13] != (byte)'H') return false;
            width = b[16] << 24 | b[17] << 16 | b[18] << 8 | b[19];
            height = b[20] << 24 | b[21] << 16 | b[22] << 8 | b[23];
            return width > 0 && height > 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void WriteU32(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var head = new byte[8];
        WriteU32(head, 0, (uint)data.Length);
        for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
        s.Write(head);
        s.Write(data);
        uint crc = Crc(head.AsSpan(4, 4), 0xFFFFFFFFu);
        crc = Crc(data, crc) ^ 0xFFFFFFFFu;
        var c = new byte[4];
        WriteU32(c, 0, crc);
        s.Write(c);
    }

    private static readonly uint[] Table = MakeTable();

    private static uint[] MakeTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc(ReadOnlySpan<byte> data, uint crc)
    {
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
