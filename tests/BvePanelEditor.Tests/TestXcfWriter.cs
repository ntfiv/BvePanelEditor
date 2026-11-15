using System.IO.Compression;
using System.Text;

namespace BvePanelEditor.Tests;

/// <summary>テスト用の XCF レイヤー指定。</summary>
public sealed class TestXcfLayer
{
    public string Name = "";
    public int Width, Height;
    public int Type = 1;              // 0=RGB、1=RGBA、2=グレー、3=グレー＋α
    public byte[] Pixels = [];        // 成分を混ぜた並び（幅×高さ×成分数）
    public int OffsetX, OffsetY;
    public int Opacity = 255;
    public bool Visible = true;
    public int Mode;
    public bool IsGroup;
    public bool Closed;
    public int[]? ItemPath;
    public bool IsText;
    public byte[]? Mask;              // 幅×高さ のグレー
    public bool ApplyMask = true;
    public bool IsFloating;           // 浮動選択（PROP_FLOATING_SELECTION）
    public int Effects;               // レイヤー効果の数（版 20 から。ダミーへのポインタ列を書く）
    public int? CompositeMode;        // PROP_COMPOSITE_MODE
    public int? CompositeSpace;       // PROP_COMPOSITE_SPACE
    public int? BlendSpace;           // PROP_BLEND_SPACE
    public int? BppOverride;          // 階層の bpp を偽る（破損データのテスト用）
    public byte[]? TileOverride;      // 最初のタイルの符号化済みバイト列を差し替える（破損データのテスト用）

    public int Components => Type switch { 0 => 3, 1 => 4, 2 => 1, _ => 2 };

    public static TestXcfLayer Solid(string name, int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = b; px[i * 4 + 3] = a;
        }
        return new TestXcfLayer { Name = name, Width = w, Height = h, Type = 1, Pixels = px };
    }
}

/// <summary>
/// 仕様どおりの最小 XCF を書く（テスト専用）。レイヤーは上から順に渡す。
/// 圧縮は 0=なし、1=RLE、2=zlib。版 11 からはポインタが 8 バイトで、精度のバイトが付く。
/// </summary>
public static class TestXcfWriter
{
    private sealed class W(MemoryStream s, int ptr)
    {
        public void U8(int v) => s.WriteByte((byte)v);
        public void U32(uint v) { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
        public void I32(int v) => U32((uint)v);
        public void Ptr(long v)
        {
            if (ptr == 4) U32((uint)v);
            else for (int i = 7; i >= 0; i--) s.WriteByte((byte)(v >> (8 * i)));
        }
        public void Bytes(byte[] b) => s.Write(b, 0, b.Length);
        public void Str(string t)
        {
            if (t.Length == 0) { U32(0); return; }
            var b = Encoding.UTF8.GetBytes(t);
            U32((uint)b.Length + 1);
            Bytes(b);
            U8(0);
        }
        public void Prop(uint type, byte[] payload) { U32(type); U32((uint)payload.Length); Bytes(payload); }
        public byte[] Done() => s.ToArray();
    }

    private static byte[] U32Bytes(params uint[] vs)
    {
        var b = new byte[vs.Length * 4];
        for (int i = 0; i < vs.Length; i++)
        {
            b[i * 4] = (byte)(vs[i] >> 24); b[i * 4 + 1] = (byte)(vs[i] >> 16); b[i * 4 + 2] = (byte)(vs[i] >> 8); b[i * 4 + 3] = (byte)vs[i];
        }
        return b;
    }

    public static void Write(string path, int width, int height, IReadOnlyList<TestXcfLayer> layersTopFirst,
        int version = 3, int compression = 1, int baseType = 0, uint precision = 150,
        IReadOnlyList<(string Name, byte[] Data)>? parasites = null, byte[]? parasitesRaw = null)
    {
        int ptr = version >= 11 ? 8 : 4;
        byte[]? parasitePayload = parasitesRaw;
        if (parasitePayload == null && parasites is { Count: > 0 })
        {
            var pw = new W(new MemoryStream(), ptr);
            foreach (var (name, data) in parasites)
            {
                pw.Str(name);
                pw.U32(1); // フラグ（永続）
                pw.U32((uint)data.Length);
                pw.Bytes(data);
            }
            parasitePayload = pw.Done();
        }
        int headerLen = 14 + 12 + (version >= 4 ? 4 : 0); // 精度は版 4 から 32bit 整数
        int propsLen = 9 + 8 + (parasitePayload != null ? 8 + parasitePayload.Length : 0); // COMPRESSION + END (+ PARASITES)
        int ptrArrayLen = (layersTopFirst.Count + 1) * ptr + ptr;
        long pos = headerLen + propsLen + ptrArrayLen;
        var blobs = new List<byte[]>();
        var bases = new long[layersTopFirst.Count];
        for (int i = 0; i < layersTopFirst.Count; i++)
        {
            bases[i] = pos;
            var blob = BuildLayer(layersTopFirst[i], pos, ptr, compression, version);
            blobs.Add(blob);
            pos += blob.Length;
        }

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        var head = new W(new MemoryStream(), ptr);
        head.Bytes(Encoding.ASCII.GetBytes("gimp xcf "));
        head.Bytes(Encoding.ASCII.GetBytes(version == 0 ? "file" : $"v{version:D3}"));
        head.U8(0);
        head.U32((uint)width);
        head.U32((uint)height);
        head.U32((uint)baseType);
        if (version >= 4) head.U32(precision);
        head.Prop(17, [(byte)compression]);
        if (parasitePayload != null) head.Prop(21, parasitePayload);
        head.Prop(0, []);
        foreach (var b in bases) head.Ptr(b);
        head.Ptr(0);
        head.Ptr(0); // チャンネル列の終端
        var hb = head.Done();
        fs.Write(hb, 0, hb.Length);
        foreach (var blob in blobs) fs.Write(blob, 0, blob.Length);
    }

    private static byte[] BuildLayer(TestXcfLayer l, long base_, int ptr, int compression, int version)
    {
        // 構造部（長さを先に知るため、ポインタは後で埋める前提で一度組み立てる）
        byte[] BuildStruct(long hierarchyPtr, long maskPtr, long effectsBase)
        {
            var w = new W(new MemoryStream(), ptr);
            w.U32((uint)l.Width);
            w.U32((uint)l.Height);
            w.U32((uint)l.Type);
            w.Str(l.Name);
            w.Prop(6, U32Bytes((uint)l.Opacity));
            if (l.Mode != 0) w.Prop(7, U32Bytes((uint)l.Mode));
            w.Prop(8, U32Bytes(l.Visible ? 1u : 0u));
            w.Prop(15, U32Bytes((uint)l.OffsetX, (uint)l.OffsetY));
            if (l.Mask != null) w.Prop(11, U32Bytes(l.ApplyMask ? 1u : 0u));
            if (l.IsFloating) w.Prop(5, new byte[ptr]);
            if (l.CompositeMode is { } cm) w.Prop(35, U32Bytes((uint)cm));
            if (l.CompositeSpace is { } cs) w.Prop(36, U32Bytes((uint)cs));
            if (l.BlendSpace is { } bs) w.Prop(37, U32Bytes((uint)bs));
            if (l.IsGroup)
            {
                w.Prop(29, []);
                w.Prop(31, U32Bytes(l.Closed ? 0u : 1u));
            }
            if (l.ItemPath != null) w.Prop(30, U32Bytes([(uint)l.ItemPath.Length, .. l.ItemPath.Select(x => (uint)x)]));
            if (l.IsText) w.Prop(26, U32Bytes(0));
            w.Prop(0, []);
            w.Ptr(hierarchyPtr);
            w.Ptr(maskPtr);
            if (version >= 20)
            {
                for (int i = 0; i < l.Effects; i++) w.Ptr(effectsBase + i * 8L);
                w.Ptr(0);
            }
            return w.Done();
        }

        int tileCall = 0;
        byte[] TileAt(int tx, int ty, int tw, int th) =>
            l.TileOverride != null && tileCall++ == 0 ? l.TileOverride : EncodeTile(l, tx, ty, tw, th, compression);
        var hier = l.IsGroup || l.Width == 0 || l.Height == 0
            ? []
            : BuildHierarchy(l.Width, l.Height, l.BppOverride ?? l.Components, TileAt, 0, ptr);
        byte[] maskStruct = [], maskHier = [];
        if (l.Mask != null)
        {
            maskHier = BuildHierarchy(l.Width, l.Height, 1,
                (tx, ty, tw, th) => EncodePlaneTile(l.Mask, l.Width, tx, ty, tw, th, compression), 0, ptr);
            // マスクのチャンネル構造は階層の手前に置く。階層ポインタは組み立て時に確定する
            maskStruct = [0]; // 長さの目安（下で作り直す）
        }

        // 長さの確定: 構造部はポインタの値に依らず一定長
        int structLen = BuildStruct(0, 0, 0).Length;
        long hierPtr = l.IsGroup || hier.Length == 0 ? 0 : base_ + structLen;
        long maskPtr = 0;
        if (l.Mask != null)
        {
            long maskBase = base_ + structLen + hier.Length;
            var mw = new W(new MemoryStream(), ptr);
            mw.U32((uint)l.Width);
            mw.U32((uint)l.Height);
            mw.Str("");
            mw.Prop(0, []);
            mw.Ptr(maskBase + (4 + 4 + 4 + 8 + ptr)); // 構造 = w,h,名前(空=4),END(8),ポインタ
            maskStruct = mw.Done();
            maskPtr = maskBase;
            // 階層側のポインタも絶対位置で作り直す
            maskHier = BuildHierarchy(l.Width, l.Height, 1,
                (tx, ty, tw, th) => EncodePlaneTile(l.Mask, l.Width, tx, ty, tw, th, compression),
                maskBase + maskStruct.Length, ptr);
        }
        if (hier.Length > 0)
        {
            tileCall = 0;
            hier = BuildHierarchy(l.Width, l.Height, l.BppOverride ?? l.Components, TileAt, hierPtr, ptr);
        }
        long effectsBase = base_ + structLen + hier.Length
            + (maskStruct.Length == 1 ? 0 : maskStruct.Length) + maskHier.Length;

        var all = new W(new MemoryStream(), ptr);
        all.Bytes(BuildStruct(hierPtr, maskPtr, effectsBase));
        all.Bytes(hier);
        all.Bytes(maskStruct.Length == 1 ? [] : maskStruct);
        all.Bytes(maskHier);
        for (int i = 0; i < l.Effects; i++) all.Bytes(new byte[8]); // 効果のダミー本体
        return all.Done();
    }

    /// <summary>階層構造（見出し＋レベル＋タイル）。absBase は階層の絶対位置。</summary>
    private static byte[] BuildHierarchy(int w, int h, int bpp, Func<int, int, int, int, byte[]> tileAt, long absBase, int ptr)
    {
        int cols = (w + 63) / 64, rows = (h + 63) / 64;
        var tiles = new List<byte[]>();
        for (int ty = 0; ty < rows; ty++)
            for (int tx = 0; tx < cols; tx++)
                tiles.Add(tileAt(tx, ty, Math.Min(64, w - tx * 64), Math.Min(64, h - ty * 64)));
        int hierLen = 12 + ptr + ptr;
        int levelLen = 8 + tiles.Count * ptr + ptr;
        long tilePos = absBase + hierLen + levelLen;
        var ms = new W(new MemoryStream(), ptr);
        ms.U32((uint)w);
        ms.U32((uint)h);
        ms.U32((uint)bpp);
        ms.Ptr(absBase + hierLen);
        ms.Ptr(0);
        ms.U32((uint)w);
        ms.U32((uint)h);
        foreach (var t in tiles)
        {
            ms.Ptr(tilePos);
            tilePos += t.Length;
        }
        ms.Ptr(0);
        foreach (var t in tiles) ms.Bytes(t);
        return ms.Done();
    }

    /// <summary>レイヤーのタイル 1 つを、圧縮方式に合わせて符号化する。</summary>
    private static byte[] EncodeTile(TestXcfLayer l, int tx, int ty, int tw, int th, int compression)
    {
        int comp = l.Components;
        var inter = new byte[tw * th * comp];
        for (int y = 0; y < th; y++)
            for (int x = 0; x < tw; x++)
            {
                int si = ((ty * 64 + y) * l.Width + tx * 64 + x) * comp;
                int di = (y * tw + x) * comp;
                Array.Copy(l.Pixels, si, inter, di, comp);
            }
        return EncodeRaw(inter, tw * th, comp, compression);
    }

    private static byte[] EncodePlaneTile(byte[] plane, int stride, int tx, int ty, int tw, int th, int compression)
    {
        var inter = new byte[tw * th];
        for (int y = 0; y < th; y++)
            for (int x = 0; x < tw; x++)
                inter[y * tw + x] = plane[(ty * 64 + y) * stride + tx * 64 + x];
        return EncodeRaw(inter, tw * th, 1, compression);
    }

    private static byte[] EncodeRaw(byte[] inter, int count, int comp, int compression)
    {
        if (compression == 0) return inter;
        if (compression == 2)
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionMode.Compress, leaveOpen: true)) z.Write(inter, 0, inter.Length);
            return ms.ToArray();
        }
        // RLE: 成分ごとに平面化して符号化
        using var outMs = new MemoryStream();
        var plane = new byte[count];
        for (int c = 0; c < comp; c++)
        {
            for (int i = 0; i < count; i++) plane[i] = inter[i * comp + c];
            var enc = RleEncode(plane);
            outMs.Write(enc, 0, enc.Length);
        }
        return outMs.ToArray();
    }

    private static byte[] RleEncode(byte[] plane)
    {
        using var ms = new MemoryStream();
        int i = 0;
        while (i < plane.Length)
        {
            int run = 1;
            while (i + run < plane.Length && plane[i + run] == plane[i] && run < 30000) run++;
            if (run >= 2)
            {
                if (run <= 127) { ms.WriteByte((byte)(run - 1)); ms.WriteByte(plane[i]); }
                else
                {
                    ms.WriteByte(127);
                    ms.WriteByte((byte)(run >> 8));
                    ms.WriteByte((byte)run);
                    ms.WriteByte(plane[i]);
                }
                i += run;
            }
            else
            {
                int start = i++;
                while (i < plane.Length && i - start < 127)
                {
                    if (i + 1 < plane.Length && plane[i] == plane[i + 1]) break;
                    i++;
                }
                int len = i - start;
                ms.WriteByte((byte)(256 - len));
                ms.Write(plane, start, len);
            }
        }
        return ms.ToArray();
    }
}
