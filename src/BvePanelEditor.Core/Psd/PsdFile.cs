using System.IO.Compression;
using System.Text;

namespace BvePanelEditor.Core.Psd;

/// <summary>キャンバス上の範囲（左上を含み、右下は含まない）。</summary>
public readonly record struct PsdRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);
    public bool IsEmpty => Width == 0 || Height == 0;

    public PsdRect Intersect(PsdRect o)
    {
        var r = new PsdRect(Math.Max(Left, o.Left), Math.Max(Top, o.Top), Math.Min(Right, o.Right), Math.Min(Bottom, o.Bottom));
        return r.IsEmpty ? default : r;
    }

    public PsdRect Union(PsdRect o)
    {
        if (IsEmpty) return o;
        if (o.IsEmpty) return this;
        return new PsdRect(Math.Min(Left, o.Left), Math.Min(Top, o.Top), Math.Max(Right, o.Right), Math.Max(Bottom, o.Bottom));
    }
}

public enum PsdLayerKind { Pixel, Text, Smart, Adjustment, Fill, FolderOpen, FolderEnd }

/// <summary>レイヤー 1 つ。画像は必要になったときに読む。</summary>
public sealed class PsdLayer
{
    public int Index;                 // ファイル内の順（0 がいちばん下）
    public string Name = "";
    /// <summary>
    /// 「フォルダ/レイヤー」の形の名前。同じ場所に同じ名前のレイヤーがあるときは、上（手前）から 2 枚目以降に「 #2」「 #3」…を付けて区別する。
    /// </summary>
    public string Path = "";
    /// <summary>「 #2」などを付ける前の「フォルダ/レイヤー」。</summary>
    public string BasePath = "";
    /// <summary>入っているフォルダ（いちばん内側）。なければ null。</summary>
    public PsdLayer? Parent;
    public PsdRect Bounds;
    public bool Hidden;
    public byte Opacity = 255;
    public byte FillOpacity = 255;
    public string BlendMode = "norm";
    /// <summary>下のレイヤーにクリッピングされている。</summary>
    public bool Clipped;
    public PsdLayerKind Kind = PsdLayerKind.Pixel;
    public bool HasEffects;
    public uint Id;

    // レイヤーマスク
    public PsdRect MaskBounds;
    public byte MaskDefault;
    public bool MaskDisabled;
    public bool HasMask => !MaskBounds.IsEmpty;

    internal readonly List<(short Id, long Offset, long Length)> Channels = [];

    public bool IsFolder => Kind == PsdLayerKind.FolderOpen;
    /// <summary>画像として使えるレイヤーか（フォルダ・調整レイヤー・空のレイヤーは使えない）。</summary>
    public bool HasPixels => Kind is PsdLayerKind.Pixel or PsdLayerKind.Text or PsdLayerKind.Smart && !Bounds.IsEmpty;

    /// <summary>調整レイヤーのとき、パネルくんで再現できる色の変え方（できなければ null）。</summary>
    public PsdAdjustment? Adjustment;
    /// <summary>調整レイヤー・塗りつぶしレイヤーの種類のキー（"levl" など）。</summary>
    public string? SpecialKey;
    /// <summary>べた塗りレイヤーの色。</summary>
    public (byte R, byte G, byte B)? SolidColor;
    internal Dictionary<string, byte[]>? Blocks;
    /// <summary>レイヤー効果（読めたとき）。HasEffects なのに null なら、読めない形式。</summary>
    public PsdLayerEffects? Effects;

    /// <summary>重ねられるか（画像のあるレイヤー、再現できる調整レイヤー、べた塗りレイヤー）。</summary>
    public bool CanCompose => HasPixels || (Kind == PsdLayerKind.Adjustment && Adjustment != null) || (Kind == PsdLayerKind.Fill && SolidColor != null);

    /// <summary>自分か、入っているフォルダのどれかが非表示。</summary>
    public bool EffectivelyHidden => Hidden || (Parent?.EffectivelyHidden ?? false);

    public override string ToString() => Path;
}

/// <summary>読み込んだレイヤーの画像（BGRA、乗算済みでない）。Bounds はキャンバス座標。</summary>
public sealed class PsdPixels(PsdRect bounds, byte[] bgra)
{
    public PsdRect Bounds { get; } = bounds;
    public byte[] Bgra { get; } = bgra;
    public int Width => Bounds.Width;
    public int Height => Bounds.Height;
}

/// <summary>PSD（版 1）を読む。RGB／グレースケール、8／16bit、無圧縮・RLE・ZIP。</summary>
public sealed class PsdFile : IDisposable
{
    public string FilePath { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Depth { get; private set; }
    public int ColorMode { get; private set; }
    public int ChannelCount { get; private set; }
    /// <summary>レイヤー（0 がいちばん下）。フォルダの開始・終了の印も含む。</summary>
    public List<PsdLayer> Layers { get; } = [];
    public List<string> Warnings { get; } = [];
    /// <summary>全体の光源（「包括光源を使用」のレイヤー効果が使う角度と高度、度）。</summary>
    public float GlobalAngle { get; private set; } = 120;
    public float GlobalAltitude { get; private set; } = 30;

    public PsdRect Canvas => new(0, 0, Width, Height);

    private readonly FileStream _fs;
    private long _mergedOffset = -1;
    private bool _mergedHasAlpha;

    // 読んだ画像の置き場（使った順に古いものから捨てる）
    private readonly LinkedList<(PsdLayer Layer, PsdPixels Pixels)> _cache = new();
    private long _cacheBytes;
    public long CacheLimitBytes { get; set; } = 600L * 1024 * 1024;

    /// <summary>描いたレイヤー効果（PsdCompose が使う）。</summary>
    internal Dictionary<PsdLayer, List<PsdEffects.Part>> EffectCache { get; } = [];
    internal long EffectCacheBytes;

    private PsdFile(string path)
    {
        FilePath = path;
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
    }

    public static PsdFile Open(string path)
    {
        BveText.EnsureInitialized(); // 古いレイヤー名（Shift_JIS）を読むため
        var f = new PsdFile(path);
        try
        {
            f.ReadStructure();
            return f;
        }
        catch
        {
            f.Dispose();
            throw;
        }
    }

    public void Dispose() => _fs.Dispose();

    /// <summary>画像として使えるレイヤー（ファイル内の順）。</summary>
    public IEnumerable<PsdLayer> PixelLayers => Layers.Where(l => l.HasPixels);

    /// <summary>重ねられるレイヤー（画像レイヤーと、再現できる調整レイヤー・べた塗りレイヤー。ファイル内の順）。</summary>
    public IEnumerable<PsdLayer> ComposableLayers => Layers.Where(l => l.CanCompose);

    /// <summary>
    /// 名前（パス）でレイヤーを探す。
    /// 1. 「#2」まで含めて同じもの　2. 「#2」を除いた名前が同じもののうち、いちばん上（手前）　3. レイヤー名だけが一致するものが 1 つならそれ。
    /// </summary>
    public PsdLayer? Find(string path)
    {
        var exact = Layers.FirstOrDefault(l => l.Kind != PsdLayerKind.FolderEnd && l.Path == path);
        if (exact != null) return exact;
        var basePath = StripDuplicateMark(path);
        var sameBase = Layers.Where(l => l.Kind != PsdLayerKind.FolderEnd && l.BasePath == basePath).ToList();
        if (sameBase.Count > 0) return sameBase.MaxBy(l => l.Index);
        var leaf = basePath.Contains('/') ? basePath[(basePath.LastIndexOf('/') + 1)..] : basePath;
        var byName = Layers.Where(l => l.Kind != PsdLayerKind.FolderEnd && l.Name == leaf).ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    /// <summary>「#2」を除いた名前が同じレイヤーを全部（下から順）。なければ Find と同じ探し方で 1 つ。</summary>
    public List<PsdLayer> FindAllSameName(string path)
    {
        var basePath = StripDuplicateMark(path);
        var all = Layers.Where(l => l.Kind != PsdLayerKind.FolderEnd && l.BasePath == basePath).ToList();
        if (all.Count > 0) return all;
        return Find(path) is { } one ? [one] : [];
    }

    /// <summary>"名前 #2" → "名前"。</summary>
    public static string StripDuplicateMark(string path) =>
        System.Text.RegularExpressions.Regex.Replace(path, @" #\d+$", "");

    // ================= 構造 =================

    private readonly byte[] _buf = new byte[8];

    private void Read(Span<byte> dst)
    {
        int n = 0;
        while (n < dst.Length)
        {
            int r = _fs.Read(dst[n..]);
            if (r <= 0) throw new InvalidDataException("PSD ファイルが途中で終わっています");
            n += r;
        }
    }

    private byte U8() { Read(_buf.AsSpan(0, 1)); return _buf[0]; }
    private ushort U16() { Read(_buf.AsSpan(0, 2)); return (ushort)(_buf[0] << 8 | _buf[1]); }
    private short I16() => (short)U16();
    private uint U32() { Read(_buf.AsSpan(0, 4)); return (uint)(_buf[0] << 24 | _buf[1] << 16 | _buf[2] << 8 | _buf[3]); }
    private int I32() => (int)U32();
    private string Ascii4() { Read(_buf.AsSpan(0, 4)); return Encoding.ASCII.GetString(_buf, 0, 4); }
    private void Skip(long n) => _fs.Seek(n, SeekOrigin.Current);

    private static readonly string[] AdjustmentKeys =
        ["levl", "curv", "hue ", "hue2", "brit", "CgEd", "blnc", "expA", "mixr", "post", "thrs", "grdm", "selc", "vibA", "phfl", "clrL", "nvrt", "blwh"];
    private static readonly string[] FillKeys = ["SoCo", "GdFl", "PtFl"];

    private void ReadStructure()
    {
        if (Ascii4() != "8BPS") throw new InvalidDataException("PSD ファイルではありません");
        int version = U16();
        if (version == 2) throw new NotSupportedException("PSB（大きなドキュメント形式）は読めません。PSD で保存し直してください");
        if (version != 1) throw new InvalidDataException($"PSD の版 {version} は読めません");
        Skip(6);
        ChannelCount = U16();
        Height = I32();
        Width = I32();
        Depth = U16();
        ColorMode = U16();
        if (ColorMode is not (1 or 3))
        {
            var name = ColorMode switch { 4 => "CMYK", 9 => "Lab", 2 => "インデックスカラー", 0 => "白黒 2 階調", 8 => "ダブルトーン", _ => $"モード {ColorMode}" };
            throw new NotSupportedException($"{name} の PSD は読めません。RGB に変換して保存し直してください");
        }
        if (Depth is not (8 or 16)) throw new NotSupportedException($"{Depth}bit の PSD は読めません。8bit か 16bit で保存し直してください");

        Skip(U32()); // カラーモードデータ
        ReadResources(U32());

        long lmLen = U32();
        long lmEnd = _fs.Position + lmLen;
        int layerCount = 0;
        if (lmLen > 0)
        {
            long liLen = U32();
            long liEnd = _fs.Position + liLen;
            if (liLen > 0) layerCount = ReadLayerInfo();
            _fs.Position = liEnd;
            // グローバルレイヤーマスク
            if (_fs.Position + 4 <= lmEnd) Skip(U32());
            // 追加情報（16bit のレイヤーは Lr16 に入っている）
            while (Layers.Count == 0 && _fs.Position + 12 <= lmEnd)
            {
                var sig = Ascii4();
                if (sig is not ("8BIM" or "8B64")) break;
                var key = Ascii4();
                long len = U32();
                long end = _fs.Position + len;
                if (key is "Lr16" or "Lr32" or "Layr") layerCount = ReadLayerInfo();
                _fs.Position = end + (len % 2);
            }
        }
        _fs.Position = lmEnd;
        _mergedOffset = _fs.Position;
        _mergedHasAlpha = ChannelCount > (ColorMode == 1 ? 1 : 3) && layerCount < 0;
        BuildTree();
    }

    /// <summary>画像リソースから全体の光源（1037 角度、1049 高度）だけ読む。</summary>
    private void ReadResources(long len)
    {
        long end = _fs.Position + len;
        while (_fs.Position + 12 <= end)
        {
            if (Ascii4() != "8BIM") break;
            int id = U16();
            int nameLen = U8();
            Skip(nameLen + ((nameLen + 1) % 2));
            long size = U32();
            long dataEnd = _fs.Position + size + (size % 2);
            if (id == 1037 && size >= 4) GlobalAngle = I32();
            else if (id == 1049 && size >= 4) GlobalAltitude = I32();
            _fs.Position = dataEnd;
        }
        _fs.Position = end;
    }

    private int ReadLayerInfo()
    {
        int count = I16();
        int n = Math.Abs(count);
        for (int i = 0; i < n; i++) Layers.Add(ReadLayerRecord(i));
        // レコードの後ろに、レイヤー順・チャンネル順で画像データが並ぶ
        foreach (var l in Layers)
        {
            for (int c = 0; c < l.Channels.Count; c++)
            {
                var ch = l.Channels[c];
                l.Channels[c] = (ch.Id, _fs.Position, ch.Length);
                Skip(ch.Length);
            }
        }
        return count;
    }

    private PsdLayer ReadLayerRecord(int index)
    {
        var l = new PsdLayer { Index = index };
        int top = I32(), left = I32(), bottom = I32(), right = I32();
        l.Bounds = new PsdRect(left, top, right, bottom);
        int nch = U16();
        for (int c = 0; c < nch; c++)
        {
            short id = I16();
            long len = U32();
            l.Channels.Add((id, 0, len));
        }
        Ascii4(); // 8BIM
        l.BlendMode = Ascii4();
        l.Opacity = U8();
        l.Clipped = U8() != 0;
        byte flags = U8();
        l.Hidden = (flags & 2) != 0;
        U8();
        long extra = U32();
        long end = _fs.Position + extra;

        // レイヤーマスク
        long maskLen = U32();
        long maskEnd = _fs.Position + maskLen;
        if (maskLen >= 20)
        {
            int mt = I32(), ml = I32(), mb = I32(), mr = I32();
            l.MaskBounds = new PsdRect(ml, mt, mr, mb);
            l.MaskDefault = U8();
            byte mf = U8();
            l.MaskDisabled = (mf & 2) != 0;
        }
        _fs.Position = maskEnd;
        Skip(U32()); // ブレンド範囲

        int nameLen = U8();
        var nameBytes = new byte[nameLen];
        Read(nameBytes);
        l.Name = Encoding.GetEncoding(932).GetString(nameBytes);
        int padded = (nameLen + 1 + 3) / 4 * 4;
        Skip(padded - (nameLen + 1));

        while (_fs.Position + 12 <= end)
        {
            var sig = Ascii4();
            if (sig is not ("8BIM" or "8B64")) break;
            var key = Ascii4();
            long len = U32();
            long dataEnd = _fs.Position + len;
            switch (key)
            {
                case "luni":
                    {
                        int chars = I32();
                        var b = new byte[chars * 2];
                        Read(b);
                        l.Name = Encoding.BigEndianUnicode.GetString(b).TrimEnd('\0');
                        break;
                    }
                case "lsct":
                case "lsdk":
                    {
                        int type = I32();
                        if (type is 1 or 2) l.Kind = PsdLayerKind.FolderOpen;
                        else if (type == 3) l.Kind = PsdLayerKind.FolderEnd;
                        if (len >= 12 && type is 1 or 2)
                        {
                            Ascii4();
                            l.BlendMode = Ascii4();
                        }
                        break;
                    }
                case "TySh": if (l.Kind == PsdLayerKind.Pixel) l.Kind = PsdLayerKind.Text; break;
                case "SoLd": case "PlLd": case "SoLE": if (l.Kind == PsdLayerKind.Pixel) l.Kind = PsdLayerKind.Smart; break;
                case "lrFX": l.HasEffects = true; break;
                case "lfx2": case "lmfx":
                    l.HasEffects = true;
                    if (len < 1 << 22)
                    {
                        var fxData = new byte[len];
                        Read(fxData);
                        (l.Blocks ??= [])[key] = fxData;
                    }
                    break;
                case "iOpa": l.FillOpacity = U8(); break;
                case "lyid": l.Id = U32(); break;
                default:
                    if (l.Kind == PsdLayerKind.Pixel && AdjustmentKeys.Contains(key)) { l.Kind = PsdLayerKind.Adjustment; l.SpecialKey = key; }
                    else if (l.Kind == PsdLayerKind.Pixel && FillKeys.Contains(key)) { l.Kind = PsdLayerKind.Fill; l.SpecialKey = key; }
                    if ((PsdAdjust.Keys.Contains(key) || key == "SoCo") && len < 1 << 20)
                    {
                        var data = new byte[len];
                        Read(data);
                        (l.Blocks ??= [])[key] = data;
                    }
                    break;
            }
            _fs.Position = dataEnd;
        }
        _fs.Position = end;
        if (l.Blocks != null)
        {
            if (l.Kind == PsdLayerKind.Adjustment) l.Adjustment = PsdAdjust.Parse(l.Blocks);
            if (l.Kind == PsdLayerKind.Fill && l.Blocks.TryGetValue("SoCo", out var soco)) l.SolidColor = ReadSolidColor(soco);
            if (l.Blocks.ContainsKey("lfx2") || l.Blocks.ContainsKey("lmfx"))
                l.Effects = PsdEffects.Parse(l.Blocks.GetValueOrDefault("lfx2"), l.Blocks.GetValueOrDefault("lmfx"), GlobalAngle, GlobalAltitude);
            l.Blocks = null;
        }
        return l;
    }

    /// <summary>べた塗りの色（RGB かグレー）。読めなければ null。</summary>
    private static (byte, byte, byte)? ReadSolidColor(byte[] data)
    {
        if (PsdDescriptor.ReadVersioned(data) is not { } d || !d.TryGetValue("Clr ", out var c) || c is not Dictionary<string, object> clr) return null;
        static byte B(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);
        if (PsdDescriptor.Number(clr, "Rd  ") is { } r && PsdDescriptor.Number(clr, "Grn ") is { } g && PsdDescriptor.Number(clr, "Bl  ") is { } b)
            return (B(r), B(g), B(b));
        if (PsdDescriptor.Number(clr, "Gry ") is { } gray)
        {
            var v = B(255 - gray * 2.55); // グレーは「黒の割合（%）」
            return (v, v, v);
        }
        return null;
    }

    /// <summary>フォルダの入れ子を組み立てる（上から順に見て、フォルダの開始で入り、終了の印で出る）。</summary>
    private void BuildTree()
    {
        var stack = new Stack<PsdLayer>();
        var usedPaths = new HashSet<string>();
        for (int i = Layers.Count - 1; i >= 0; i--)
        {
            var l = Layers[i];
            if (l.Kind == PsdLayerKind.FolderEnd)
            {
                if (stack.Count > 0) stack.Pop();
                continue;
            }
            l.Parent = stack.Count > 0 ? stack.Peek() : null;
            l.BasePath = l.Parent != null ? $"{l.Parent.Path}/{l.Name}" : l.Name;
            // 同じ場所に同じ名前が既にあれば（上から数えて 2 枚目以降）「 #2」…を付ける
            var path = l.BasePath;
            for (int n = 2; usedPaths.Contains(path); n++) path = $"{l.BasePath} #{n}";
            usedPaths.Add(path);
            l.Path = path;
            if (l.Kind == PsdLayerKind.FolderOpen) stack.Push(l);
        }
    }

    // ================= 画像 =================

    private readonly Dictionary<PsdLayer, PsdMask?> _masks = [];

    /// <summary>レイヤー（フォルダ・調整レイヤー・塗りつぶしレイヤーも）のマスク。ない・使わない設定なら null。</summary>
    public PsdMask? GetMask(PsdLayer layer)
    {
        if (_masks.TryGetValue(layer, out var m)) return m;
        m = null;
        if (layer.HasMask && !layer.MaskDisabled)
            foreach (var (id, offset, length) in layer.Channels)
                if (id == -2)
                {
                    m = new PsdMask(layer.MaskBounds, DecodeChannel(offset, length, layer.MaskBounds.Width, layer.MaskBounds.Height), layer.MaskDefault);
                    break;
                }
        _masks[layer] = m;
        return m;
    }

    /// <summary>レイヤーの画像（マスク適用済み、不透明度は掛けていない）。画像がなければ null。</summary>
    public PsdPixels? GetPixels(PsdLayer layer)
    {
        if (layer.Bounds.IsEmpty || layer.Channels.Count == 0) return null;
        for (var node = _cache.First; node != null; node = node.Next)
        {
            if (node.Value.Layer != layer) continue;
            _cache.Remove(node);
            _cache.AddFirst(node);
            return node.Value.Pixels;
        }
        var px = DecodeLayer(layer);
        _cache.AddFirst((layer, px));
        _cacheBytes += px.Bgra.Length;
        while (_cacheBytes > CacheLimitBytes && _cache.Count > 1)
        {
            var last = _cache.Last!;
            _cacheBytes -= last.Value.Pixels.Bgra.Length;
            _cache.RemoveLast();
        }
        return px;
    }

    private PsdPixels DecodeLayer(PsdLayer layer)
    {
        int w = layer.Bounds.Width, h = layer.Bounds.Height;
        var bgra = new byte[w * h * 4];
        bool hasAlpha = false;
        foreach (var (id, offset, length) in layer.Channels)
        {
            if (id <= -2) continue; // マスク（-2）は色とアルファを読んだ後に掛ける
            var plane = DecodeChannel(offset, length, w, h);
            if (id == -1) { CopyPlane(plane, bgra, 3); hasAlpha = true; }
            else if (ColorMode == 1 && id == 0) { CopyPlane(plane, bgra, 0); CopyPlane(plane, bgra, 1); CopyPlane(plane, bgra, 2); }
            else if (id is >= 0 and <= 2) CopyPlane(plane, bgra, 2 - id); // R→2, G→1, B→0
        }
        if (!hasAlpha)
            for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
        foreach (var (id, offset, length) in layer.Channels)
        {
            if (id != -2 || !layer.HasMask || layer.MaskDisabled) continue;
            var m = DecodeChannel(offset, length, layer.MaskBounds.Width, layer.MaskBounds.Height);
            ApplyMask(bgra, layer.Bounds, m, layer.MaskBounds, layer.MaskDefault);
        }
        return new PsdPixels(layer.Bounds, bgra);
    }

    private static void CopyPlane(byte[] plane, byte[] bgra, int offset)
    {
        for (int i = 0, j = offset; i < plane.Length && j < bgra.Length; i++, j += 4) bgra[j] = plane[i];
    }

    private static void ApplyMask(byte[] bgra, PsdRect layerRect, byte[] mask, PsdRect maskRect, byte def)
    {
        int w = layerRect.Width, h = layerRect.Height, mw = maskRect.Width;
        for (int y = 0; y < h; y++)
        {
            int cy = layerRect.Top + y;
            for (int x = 0; x < w; x++)
            {
                int cx = layerRect.Left + x;
                byte m = cx >= maskRect.Left && cx < maskRect.Right && cy >= maskRect.Top && cy < maskRect.Bottom
                    ? mask[(cy - maskRect.Top) * mw + (cx - maskRect.Left)] : def;
                int a = (y * w + x) * 4 + 3;
                bgra[a] = (byte)(bgra[a] * m / 255);
            }
        }
    }

    /// <summary>1 チャンネルを 8bit の平面に読む。</summary>
    private byte[] DecodeChannel(long offset, long length, int w, int h)
    {
        int bps = Depth / 8;
        var outPlane = new byte[w * h];
        if (w == 0 || h == 0 || length < 2) return outPlane;
        _fs.Position = offset;
        int comp = U16();
        var raw = DecodeData(comp, length - 2, w, h, bps);
        if (bps == 1) Buffer.BlockCopy(raw, 0, outPlane, 0, Math.Min(raw.Length, outPlane.Length));
        else for (int i = 0; i < outPlane.Length && i * 2 + 1 < raw.Length; i++) outPlane[i] = Round16(raw[i * 2], raw[i * 2 + 1]);
        return outPlane;
    }

    /// <summary>16bit の値を 8bit に丸める。</summary>
    private static byte Round16(byte hi, byte lo) => (byte)(((hi << 8 | lo) * 255 + 32767) / 65535);

    /// <summary>圧縮をほどいて、1 行 w×bps バイトの h 行を返す。</summary>
    private byte[] DecodeData(int comp, long length, int w, int h, int bps)
    {
        int rowBytes = w * bps;
        var result = new byte[rowBytes * h];
        switch (comp)
        {
            case 0:
                Read(result.AsSpan(0, (int)Math.Min(result.Length, length)));
                break;
            case 1:
                {
                    var counts = new int[h];
                    for (int y = 0; y < h; y++) counts[y] = U16();
                    int max = counts.Length == 0 ? 0 : counts.Max();
                    var row = new byte[max];
                    for (int y = 0; y < h; y++)
                    {
                        Read(row.AsSpan(0, counts[y]));
                        UnpackBits(row, counts[y], result, y * rowBytes, rowBytes);
                    }
                    break;
                }
            case 2:
            case 3:
                {
                    var data = new byte[length];
                    Read(data);
                    using (var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress))
                    {
                        int n = 0, r;
                        while (n < result.Length && (r = z.Read(result, n, result.Length - n)) > 0) n += r;
                    }
                    if (comp == 3) Unpredict(result, w, h, bps);
                    break;
                }
            default:
                throw new InvalidDataException($"PSD の圧縮方式 {comp} は読めません");
        }
        return result;
    }

    private static void UnpackBits(byte[] src, int srcLen, byte[] dst, int dstOffset, int dstLen)
    {
        int i = 0, o = dstOffset, end = dstOffset + dstLen;
        while (i < srcLen && o < end)
        {
            sbyte n = (sbyte)src[i++];
            if (n >= 0)
            {
                int count = n + 1;
                for (int k = 0; k < count && i < srcLen && o < end; k++) dst[o++] = src[i++];
            }
            else if (n != -128)
            {
                int count = -n + 1;
                byte v = i < srcLen ? src[i++] : (byte)0;
                for (int k = 0; k < count && o < end; k++) dst[o++] = v;
            }
        }
    }

    /// <summary>ZIP（予測付き）の差分を戻す。</summary>
    private static void Unpredict(byte[] data, int w, int h, int bps)
    {
        if (bps == 1)
        {
            for (int y = 0; y < h; y++)
                for (int x = 1, i = y * w + 1; x < w; x++, i++) data[i] = (byte)(data[i] + data[i - 1]);
        }
        else
        {
            for (int y = 0; y < h; y++)
            {
                int row = y * w * 2;
                for (int x = 1; x < w; x++)
                {
                    int i = row + x * 2;
                    int prev = data[i - 2] << 8 | data[i - 1];
                    int cur = (data[i] << 8 | data[i + 1]) + prev;
                    data[i] = (byte)(cur >> 8);
                    data[i + 1] = (byte)cur;
                }
            }
        }
    }

    /// <summary>PSD 全体を重ねた絵（統合画像）。保存時に「互換性を優先」していないと白や空のことがある。</summary>
    public PsdPixels ReadMerged()
    {
        int w = Width, h = Height, bps = Depth / 8;
        var bgra = new byte[w * h * 4];
        _fs.Position = _mergedOffset;
        int comp = U16();
        int colorCh = ColorMode == 1 ? 1 : 3;
        int useCh = Math.Min(ChannelCount, colorCh + (_mergedHasAlpha ? 1 : 0));
        int rowBytes = w * bps;
        var planes = new List<byte[]>();
        if (comp == 1)
        {
            var counts = new int[ChannelCount * h];
            for (int i = 0; i < counts.Length; i++) counts[i] = U16();
            var row = new byte[counts.Length == 0 ? 0 : counts.Max()];
            for (int c = 0; c < useCh; c++)
            {
                var plane = new byte[rowBytes * h];
                for (int y = 0; y < h; y++)
                {
                    int n = counts[c * h + y];
                    Read(row.AsSpan(0, n));
                    UnpackBits(row, n, plane, y * rowBytes, rowBytes);
                }
                planes.Add(plane);
            }
        }
        else if (comp == 0)
        {
            for (int c = 0; c < useCh; c++)
            {
                var plane = new byte[rowBytes * h];
                Read(plane);
                planes.Add(plane);
            }
        }
        else if (comp is 2 or 3)
        {
            // ZIP: 残り全部が全チャンネルぶんの圧縮データ
            var data = new byte[_fs.Length - _fs.Position];
            Read(data);
            var all = new byte[rowBytes * h * ChannelCount];
            using (var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress))
            {
                int n = 0, r;
                while (n < all.Length && (r = z.Read(all, n, all.Length - n)) > 0) n += r;
            }
            if (comp == 3) Unpredict(all, w, h * ChannelCount, bps);
            for (int c = 0; c < useCh; c++) planes.Add(all.AsSpan(c * rowBytes * h, rowBytes * h).ToArray());
        }
        else throw new InvalidDataException($"統合画像の圧縮方式 {comp} は読めません");

        for (int c = 0; c < planes.Count; c++)
        {
            var p = planes[c];
            int dst = c == colorCh ? 3 : ColorMode == 1 ? -1 : 2 - c;
            for (int i = 0; i < w * h; i++)
            {
                byte v = bps == 1 ? p[i] : Round16(p[i * 2], p[i * 2 + 1]);
                if (dst == -1) { bgra[i * 4] = bgra[i * 4 + 1] = bgra[i * 4 + 2] = v; }
                else bgra[i * 4 + dst] = v;
            }
        }
        if (planes.Count <= colorCh)
            for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
        return new PsdPixels(Canvas, bgra);
    }
}

/// <summary>マスク 1 枚（キャンバス座標の範囲と、範囲外の値）。</summary>
public sealed class PsdMask(PsdRect bounds, byte[] plane, byte outside)
{
    public PsdRect Bounds { get; } = bounds;
    public byte Outside { get; } = outside;

    /// <summary>キャンバスの (x, y) での値（0〜1）。</summary>
    public float At(int x, int y) =>
        (x >= Bounds.Left && x < Bounds.Right && y >= Bounds.Top && y < Bounds.Bottom
            ? plane[(y - Bounds.Top) * Bounds.Width + (x - Bounds.Left)] : Outside) / 255f;
}
