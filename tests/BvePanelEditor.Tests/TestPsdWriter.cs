using System.IO.Compression;
using System.Text;
using BvePanelEditor.Core.Psd;

namespace BvePanelEditor.Tests;

/// <summary>テスト用のレイヤー。</summary>
public sealed class TestLayer
{
    public string Name = "Layer";
    public PsdRect Bounds;
    public byte[] Bgra = [];
    public bool Hidden;
    public byte Opacity = 255;
    public bool Clipped;
    public string Blend = "norm";
    /// <summary>0 = 普通、1 = フォルダ（開始）、3 = フォルダの終わりの印。</summary>
    public int SectionType;
    public bool Rle = true;
    public PsdRect MaskBounds;
    public byte[]? Mask;
    /// <summary>追加情報（キーとデータ）。調整レイヤーや塗りつぶしレイヤーを作るときに使う。</summary>
    public List<(string Key, byte[] Data)> Blocks = [];

    public static TestLayer Solid(string name, PsdRect r, byte red, byte green, byte blue, byte alpha = 255)
    {
        var px = new byte[r.Width * r.Height * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = blue; px[i + 1] = green; px[i + 2] = red; px[i + 3] = alpha; }
        return new TestLayer { Name = name, Bounds = r, Bgra = px };
    }

    public static TestLayer FolderStart(string name) => new() { Name = name, SectionType = 1 };
    public static TestLayer FolderEnd() => new() { Name = "</Layer group>", SectionType = 3 };
}

/// <summary>テスト用の小さな PSD 書き出し（RGB 8bit、無圧縮か RLE）。レイヤーは下から順。</summary>
public static class TestPsdWriter
{
    public static void Write(string path, int width, int height, IReadOnlyList<TestLayer> layers)
    {
        using var f = File.Create(path);
        var w = new BinaryWriterBE(f);
        w.Ascii("8BPS"); w.U16(1); w.Zero(6); w.U16(4); w.U32((uint)height); w.U32((uint)width); w.U16(8); w.U16(3);
        w.U32(0); // カラーモード
        w.U32(0); // 画像リソース

        var li = new MemoryStream();
        var lw = new BinaryWriterBE(li);
        lw.I16((short)-layers.Count);
        var channelData = new List<List<(short Id, byte[] Data)>>();
        foreach (var l in layers)
        {
            var chans = new List<(short, byte[])>();
            int lw_ = l.Bounds.Width, lh = l.Bounds.Height;
            if (l.SectionType == 0)
            {
                chans.Add((-1, Channel(Plane(l.Bgra, 3), lw_, lh, l.Rle)));
                chans.Add((0, Channel(Plane(l.Bgra, 2), lw_, lh, l.Rle)));
                chans.Add((1, Channel(Plane(l.Bgra, 1), lw_, lh, l.Rle)));
                chans.Add((2, Channel(Plane(l.Bgra, 0), lw_, lh, l.Rle)));
                if (l.Mask != null) chans.Add((-2, Channel(l.Mask, l.MaskBounds.Width, l.MaskBounds.Height, l.Rle)));
            }
            else
            {
                foreach (short id in new short[] { -1, 0, 1, 2 }) chans.Add((id, Channel([], 0, 0, false)));
                if (l.Mask != null) chans.Add((-2, Channel(l.Mask, l.MaskBounds.Width, l.MaskBounds.Height, l.Rle)));
            }
            channelData.Add(chans);

            lw.I32(l.Bounds.Top); lw.I32(l.Bounds.Left); lw.I32(l.Bounds.Bottom); lw.I32(l.Bounds.Right);
            lw.U16((ushort)chans.Count);
            foreach (var (id, data) in chans) { lw.I16(id); lw.U32((uint)data.Length); }
            lw.Ascii("8BIM"); lw.Ascii(l.Blend);
            lw.U8(l.Opacity); lw.U8((byte)(l.Clipped ? 1 : 0)); lw.U8((byte)((l.Hidden ? 2 : 0) | (l.SectionType != 0 ? 0x18 : 0x08))); lw.U8(0);

            var extra = new MemoryStream();
            var ew = new BinaryWriterBE(extra);
            if (l.Mask != null)
            {
                ew.U32(20);
                ew.I32(l.MaskBounds.Top); ew.I32(l.MaskBounds.Left); ew.I32(l.MaskBounds.Bottom); ew.I32(l.MaskBounds.Right);
                ew.U8(0); ew.U8(0); ew.Zero(2);
            }
            else ew.U32(0);
            ew.U32(0); // ブレンド範囲
            var ascii = Encoding.ASCII.GetBytes("L");
            ew.U8((byte)ascii.Length); extra.Write(ascii);
            ew.Zero((4 - (ascii.Length + 1) % 4) % 4);
            // Unicode の名前
            var uni = Encoding.BigEndianUnicode.GetBytes(l.Name);
            int ulen = 4 + uni.Length;
            ew.Ascii("8BIM"); ew.Ascii("luni"); ew.U32((uint)(ulen + ulen % 2)); ew.U32((uint)l.Name.Length); extra.Write(uni); if (ulen % 2 == 1) ew.U8(0);
            if (l.SectionType != 0) { ew.Ascii("8BIM"); ew.Ascii("lsct"); ew.U32(4); ew.U32((uint)l.SectionType); }
            foreach (var (key, data) in l.Blocks)
            {
                ew.Ascii("8BIM"); ew.Ascii(key); ew.U32((uint)(data.Length + data.Length % 2)); extra.Write(data); if (data.Length % 2 == 1) ew.U8(0);
            }
            lw.U32((uint)extra.Length);
            li.Write(extra.ToArray());
        }
        foreach (var chans in channelData)
            foreach (var (_, data) in chans) li.Write(data);
        if (li.Length % 2 == 1) li.WriteByte(0);

        var lm = new MemoryStream();
        var mw = new BinaryWriterBE(lm);
        mw.U32((uint)li.Length);
        lm.Write(li.ToArray());
        mw.U32(0); // グローバルマスク
        w.U32((uint)lm.Length);
        f.Write(lm.ToArray());

        // 統合画像（無圧縮、白）
        w.U16(0);
        for (int c = 0; c < 4; c++) for (int i = 0; i < width * height; i++) w.U8(c == 3 ? (byte)255 : (byte)255);
    }

    private static byte[] Plane(byte[] bgra, int offset)
    {
        var p = new byte[bgra.Length / 4];
        for (int i = 0; i < p.Length; i++) p[i] = bgra[i * 4 + offset];
        return p;
    }

    private static byte[] Channel(byte[] plane, int w, int h, bool rle)
    {
        var ms = new MemoryStream();
        var bw = new BinaryWriterBE(ms);
        if (!rle || w == 0 || h == 0)
        {
            bw.U16(0);
            ms.Write(plane);
            return ms.ToArray();
        }
        bw.U16(1);
        var rows = new List<byte[]>();
        for (int y = 0; y < h; y++) rows.Add(PackBits(plane.AsSpan(y * w, w)));
        foreach (var r in rows) bw.U16((ushort)r.Length);
        foreach (var r in rows) ms.Write(r);
        return ms.ToArray();
    }

    /// <summary>PackBits（同じ値の並びは繰り返しにする）。</summary>
    public static byte[] PackBits(ReadOnlySpan<byte> src)
    {
        var o = new List<byte>();
        int i = 0;
        while (i < src.Length)
        {
            int run = 1;
            while (i + run < src.Length && run < 128 && src[i + run] == src[i]) run++;
            if (run >= 3)
            {
                o.Add((byte)(sbyte)(1 - run));
                o.Add(src[i]);
                i += run;
                continue;
            }
            int start = i, lit = 0;
            while (i < src.Length && lit < 128)
            {
                if (i + 2 < src.Length && src[i] == src[i + 1] && src[i] == src[i + 2]) break;
                i++; lit++;
            }
            o.Add((byte)(lit - 1));
            for (int k = start; k < start + lit; k++) o.Add(src[k]);
        }
        return o.ToArray();
    }

    private sealed class BinaryWriterBE(Stream s)
    {
        public void U8(byte v) => s.WriteByte(v);
        public void U16(ushort v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
        public void I16(short v) => U16((ushort)v);
        public void U32(uint v) { U16((ushort)(v >> 16)); U16((ushort)v); }
        public void I32(int v) => U32((uint)v);
        public void Ascii(string t) => s.Write(Encoding.ASCII.GetBytes(t));
        public void Zero(int n) { for (int i = 0; i < n; i++) s.WriteByte(0); }
    }
}

/// <summary>テスト用の PNG 読み込み（PngWriter が書く RGBA・Sub フィルタだけ）。</summary>
public static class TestPngReader
{
    public static (int W, int H, byte[] Rgba) Read(byte[] png)
    {
        int pos = 8, w = 0, h = 0;
        var idat = new MemoryStream();
        while (pos < png.Length)
        {
            int len = png[pos] << 24 | png[pos + 1] << 16 | png[pos + 2] << 8 | png[pos + 3];
            var type = Encoding.ASCII.GetString(png, pos + 4, 4);
            if (type == "IHDR")
            {
                w = png[pos + 8] << 24 | png[pos + 9] << 16 | png[pos + 10] << 8 | png[pos + 11];
                h = png[pos + 12] << 24 | png[pos + 13] << 16 | png[pos + 14] << 8 | png[pos + 15];
            }
            else if (type == "IDAT") idat.Write(png, pos + 8, len);
            pos += 12 + len;
        }
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        var raw = new MemoryStream();
        z.CopyTo(raw);
        var data = raw.ToArray();
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int o = y * (w * 4 + 1);
            if (data[o] != 1) throw new InvalidDataException("Sub 以外のフィルタ");
            for (int x = 0; x < w * 4; x++)
            {
                byte v = data[o + 1 + x];
                if (x >= 4) v = (byte)(v + rgba[y * w * 4 + x - 4]);
                rgba[y * w * 4 + x] = v;
            }
        }
        return (w, h, rgba);
    }
}
