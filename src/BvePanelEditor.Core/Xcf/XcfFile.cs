using System.IO.Compression;
using System.Text;
using BvePanelEditor.Core.Psd;

namespace BvePanelEditor.Core.Xcf;

/// <summary>XCF のレイヤーについて、PSD のモデルには無い追加情報（描画モードの写像結果など）。</summary>
public sealed class XcfLayerInfo
{
    /// <summary>GIMP の描画モード番号（ファイルに書かれていた値）。</summary>
    public int Mode;
    /// <summary>PSD のキーへ写したが、式が完全には一致しない（△近似）。</summary>
    public bool ModeApprox;
    /// <summary>PSD 側に写せる描画モードが無い（×非対応。通常として重ねる）。</summary>
    public bool ModeUnsupported;
}

/// <summary>
/// XCF（GIMP の形式）を読む。RGB／グレースケール、8bit 整数のみ。
/// レイヤーの持ち方は PSD と同じ（<see cref="PsdLayer"/> の並び、0 がいちばん下、フォルダは開始・終了の印）にして、
/// 合成・取り込みの既存コードをそのまま使えるようにする。画素は必要になったときに読む。
/// 仕様: GIMP 付属の XCF 仕様書（Makholm）と GIMP 2.10/3 の読み書きコードを参照。
/// </summary>
public sealed class XcfFile : IDisposable
{
    // プロパティ番号（xcf-private.h）
    private const int PropEnd = 0, PropOpacity = 6, PropMode = 7, PropVisible = 8, PropApplyMask = 11,
        PropOffsets = 15, PropCompression = 17, PropTextLayerFlags = 26, PropGroupItem = 29,
        PropItemPath = 30, PropGroupItemFlags = 31, PropFloatOpacity = 33, PropFloatingSelection = 5;

    public string FilePath { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    /// <summary>XCF の版（0〜23。GIMP 2.10 世代は 11 前後、GIMP 3 世代は 14〜23）。</summary>
    public int Version { get; private set; }
    /// <summary>0=RGB、1=グレースケール。</summary>
    public int BaseType { get; private set; }
    /// <summary>レイヤー（0 がいちばん下）。フォルダの開始・終了の印も含む。</summary>
    public List<PsdLayer> Layers { get; } = [];
    public List<string> Warnings { get; } = [];
    /// <summary>レイヤーごとの追加情報（描画モードの写像結果）。</summary>
    public IReadOnlyDictionary<PsdLayer, XcfLayerInfo> LayerInfo => _info;

    public PsdRect Canvas => new(0, 0, Width, Height);

    private readonly FileStream _fs;
    private readonly Dictionary<PsdLayer, XcfLayerInfo> _info = [];
    private readonly Dictionary<PsdLayer, RawLayer> _raw = [];
    private readonly Dictionary<PsdLayer, PsdPixels> _cache = [];
    private readonly Dictionary<PsdLayer, PsdMask?> _masks = [];
    private int _compression = 1; // 0=なし、1=RLE、2=zlib
    private int _ptrSize = 4;     // 版 11 から 8

    /// <summary>レイヤー 1 つの、画素を読むための控え。</summary>
    private sealed class RawLayer
    {
        public long Hierarchy;      // 画素の階層構造へのポインタ（0 = なし）
        public long MaskHierarchy;  // マスク（チャンネル構造）の階層へのポインタ（0 = なし）
        public int Type;            // 0=RGB、1=RGBA、2=グレー、3=グレー＋α
        public int Width, Height;
        public bool ApplyMask = true;
    }

    private XcfFile(string path)
    {
        FilePath = path;
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
    }

    public static XcfFile Open(string path)
    {
        var f = new XcfFile(path);
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

    /// <summary>重ねられるレイヤー（ファイル内の順）。</summary>
    public IEnumerable<PsdLayer> ComposableLayers => Layers.Where(l => l.CanCompose);

    // 名前で探すための索引（PsdFile と同じ探し方）
    private Dictionary<string, PsdLayer>? _byPath;
    private Dictionary<string, List<PsdLayer>>? _byBasePath;
    private Dictionary<string, List<PsdLayer>>? _byName;

    private void EnsureIndex()
    {
        if (_byPath != null) return;
        _byPath = [];
        _byBasePath = [];
        _byName = [];
        foreach (var l in Layers)
        {
            if (l.Kind == PsdLayerKind.FolderEnd) continue;
            _byPath[l.Path] = l;
            if (!_byBasePath.TryGetValue(l.BasePath, out var b)) _byBasePath[l.BasePath] = b = [];
            b.Add(l);
            if (!_byName.TryGetValue(l.Name, out var n)) _byName[l.Name] = n = [];
            n.Add(l);
        }
    }

    /// <summary>名前（パス）でレイヤーを探す。探し方は <see cref="PsdFile.Find"/> と同じ。</summary>
    public PsdLayer? Find(string path)
    {
        EnsureIndex();
        if (_byPath!.TryGetValue(path, out var exact)) return exact;
        var basePath = PsdFile.StripDuplicateMark(path);
        if (_byBasePath!.TryGetValue(basePath, out var sameBase) && sameBase.Count > 0) return sameBase[^1];
        var leaf = basePath.Contains('/') ? basePath[(basePath.LastIndexOf('/') + 1)..] : basePath;
        return _byName!.TryGetValue(leaf, out var byName) && byName.Count == 1 ? byName[0] : null;
    }

    // ================= 構造 =================

    private readonly byte[] _buf = new byte[8];

    private void Read(Span<byte> dst)
    {
        int n = 0;
        while (n < dst.Length)
        {
            int r = _fs.Read(dst[n..]);
            if (r <= 0) throw new InvalidDataException("XCF ファイルが途中で終わっています");
            n += r;
        }
    }

    private byte U8() { Read(_buf.AsSpan(0, 1)); return _buf[0]; }
    private uint U32() { Read(_buf.AsSpan(0, 4)); return (uint)(_buf[0] << 24 | _buf[1] << 16 | _buf[2] << 8 | _buf[3]); }
    private int I32() => (int)U32();
    private long Ptr()
    {
        if (_ptrSize == 4) return U32();
        Read(_buf);
        return (long)((ulong)_buf[0] << 56 | (ulong)_buf[1] << 48 | (ulong)_buf[2] << 40 | (ulong)_buf[3] << 32
            | (ulong)_buf[4] << 24 | (ulong)_buf[5] << 16 | (ulong)_buf[6] << 8 | _buf[7]);
    }
    private float F32() { Read(_buf.AsSpan(0, 4)); return BitConverter.Int32BitsToSingle((int)U32FromBuf()); }
    private uint U32FromBuf() => (uint)(_buf[0] << 24 | _buf[1] << 16 | _buf[2] << 8 | _buf[3]);

    private string ReadString()
    {
        uint n = U32();
        if (n == 0) return "";
        if (n > 1 << 20) throw new InvalidDataException("名前が長すぎます");
        var b = new byte[n];
        Read(b);
        return Encoding.UTF8.GetString(b, 0, (int)n - 1); // 末尾は 0
    }

    /// <summary>プロパティ列を読む。type が 0 で終わる。known には読んだプロパティの（番号, ペイロード先頭位置, 長さ）を渡す。</summary>
    private void ReadProps(Action<int, long, uint> known)
    {
        while (true)
        {
            uint type = U32();
            uint len = U32();
            if (type == PropEnd) return;
            long start = _fs.Position;
            if (start + len > _fs.Length) throw new InvalidDataException("プロパティの長さがファイルの範囲外です");
            known((int)type, start, len);
            _fs.Position = start + len;
        }
    }

    private void ReadStructure()
    {
        var magic = new byte[14];
        Read(magic);
        if (Encoding.ASCII.GetString(magic, 0, 9) != "gimp xcf ") throw new InvalidDataException("XCF ファイルではありません");
        var verText = Encoding.ASCII.GetString(magic, 9, 4);
        Version = verText == "file" ? 0 : verText.StartsWith('v') && int.TryParse(verText[1..], out var v) ? v : -1;
        if (Version < 0) throw new InvalidDataException($"XCF の版（{verText}）が読めません");
        if (Version >= 100) throw new NotSupportedException("CinePaint の XCF は読めません");
        if (Version > 23) Warnings.Add($"想定より新しい XCF の版（{Version}）です。読める範囲で読みます");
        _ptrSize = Version >= 11 ? 8 : 4;

        Width = I32();
        Height = I32();
        BaseType = I32();
        if (BaseType == 2) throw new NotSupportedException("インデックスカラーの XCF は読めません。RGB に変換して保存し直してください");
        if (BaseType is not (0 or 1)) throw new InvalidDataException($"色の種類（{BaseType}）が不正です");
        if (Width is < 1 or > 30000 || Height is < 1 or > 30000)
            throw new InvalidDataException($"キャンバスの大きさ（{Width}×{Height}）が不正です");
        if (Version >= 11)
        {
            int precision = U8();
            // 0=8bit リニア、1=8bit ガンマ（非リニア）。2 以上は 16bit 以上
            if (precision > 1)
                throw new NotSupportedException("16bit 以上の精度の XCF は読めません。8bit で保存し直してください");
        }

        ReadProps((type, start, len) =>
        {
            if (type == PropCompression && len >= 1)
            {
                _fs.Position = start;
                _compression = U8();
            }
        });
        if (_compression is not (0 or 1 or 2))
            throw new NotSupportedException($"XCF の圧縮方式 {_compression} は読めません");

        var layerPtrs = new List<long>();
        while (true)
        {
            long p = Ptr();
            if (p == 0) break;
            if (p < 0 || p >= _fs.Length) throw new InvalidDataException("レイヤーの位置がファイルの範囲外です");
            layerPtrs.Add(p);
        }
        while (Ptr() != 0) { } // チャンネルは使わない（マスクはレイヤー側からたどる）

        var records = new List<LayerRecord>();
        foreach (var p in layerPtrs) records.Add(ReadLayerRecord(p));
        BuildLayers(records);
    }

    /// <summary>レイヤー構造 1 つ分の読み取り結果（並べ替え前、上から順）。</summary>
    private sealed class LayerRecord
    {
        public PsdLayer Layer = new();
        public RawLayer Raw = new();
        public XcfLayerInfo Info = new();
        public int[]? ItemPath;   // 自分自身のパス（ルートから）。無いファイルもある
        public int[] FullPath = []; // ItemPath を補完した、組み立て用のパス
        public bool IsGroup;
        public bool Floating;
    }

    private LayerRecord ReadLayerRecord(long offset)
    {
        _fs.Position = offset;
        var rec = new LayerRecord();
        var l = rec.Layer;
        int w = I32(), h = I32(), type = I32();
        if (w < 0 || h < 0 || (long)w * h > int.MaxValue / 4) throw new InvalidDataException("レイヤーが大きすぎます");
        rec.Raw.Width = w;
        rec.Raw.Height = h;
        rec.Raw.Type = type;
        l.Name = ReadString();
        int mode = 0;
        float? floatOpacity = null;
        bool isText = false;
        ReadProps((pt, start, len) =>
        {
            _fs.Position = start;
            switch (pt)
            {
                case PropOpacity: if (len >= 4) l.Opacity = (byte)Math.Clamp(U32(), 0u, 255u); break;
                case PropFloatOpacity: if (len >= 4) floatOpacity = F32(); break;
                case PropMode: if (len >= 4) mode = I32(); break;
                case PropVisible: if (len >= 4) l.Hidden = U32() == 0; break;
                case PropApplyMask: if (len >= 4) rec.Raw.ApplyMask = U32() != 0; break;
                case PropOffsets: if (len >= 8) { int x = I32(), y = I32(); l.Bounds = new PsdRect(x, y, x + w, y + h); } break;
                case PropGroupItem: rec.IsGroup = true; break;
                case PropGroupItemFlags: if (len >= 4) l.Closed = (U32() & 1) == 0; break; // bit0 = 開いている
                case PropItemPath:
                    if (len >= 4)
                    {
                        int n = I32();
                        if (n is >= 0 and <= 64 && len >= 4 + 4 * n)
                        {
                            rec.ItemPath = new int[n];
                            for (int i = 0; i < n; i++) rec.ItemPath[i] = I32();
                        }
                    }
                    break;
                case PropTextLayerFlags: isText = true; break;
                case PropFloatingSelection: rec.Floating = true; break;
            }
        });
        if (l.Bounds == default) l.Bounds = new PsdRect(0, 0, w, h);
        if (floatOpacity is { } fo) l.Opacity = (byte)Math.Clamp((int)Math.Round(fo * 255), 0, 255);
        rec.Raw.Hierarchy = Ptr();
        long maskPtr = Ptr();
        if (maskPtr != 0) rec.Raw.MaskHierarchy = ReadMaskHierarchy(maskPtr, l);
        if (rec.IsGroup) l.Kind = PsdLayerKind.FolderOpen;
        else if (isText) l.Kind = PsdLayerKind.Text;
        l.BlendMode = MapBlendMode(mode, out bool approx, out bool unsupported);
        rec.Info.Mode = mode;
        rec.Info.ModeApprox = approx;
        rec.Info.ModeUnsupported = unsupported;
        return rec;
    }

    /// <summary>マスク（チャンネル構造）を読み、階層へのポインタを返す。マスクの範囲はレイヤーと同じ場所にする。</summary>
    private long ReadMaskHierarchy(long offset, PsdLayer layer)
    {
        _fs.Position = offset;
        int w = I32(), h = I32();
        _ = ReadString();
        ReadProps((_, _, _) => { });
        long hierarchy = Ptr();
        if (w > 0 && h > 0)
        {
            layer.MaskBounds = layer.Bounds;
            layer.MaskDefault = 0;
            layer.MaskDisabled = false; // 適用するかは Raw.ApplyMask で見る
        }
        return hierarchy;
    }

    /// <summary>上から順のレコード列を、PSD と同じ並び（0 がいちばん下＋フォルダの開始・終了の印）に組み替える。</summary>
    private void BuildLayers(List<LayerRecord> records)
    {
        var kept = new List<LayerRecord>();
        foreach (var r in records)
        {
            if (r.Floating)
            {
                Warnings.Add($"浮動選択「{r.Layer.Name}」は読み飛ばしました");
                continue;
            }
            kept.Add(r);
        }

        // 子の並び（上から順）を ITEM_PATH から作る。
        // パスが付いていないレコードはルートのレイヤーとして、ファイル順に通し番号を振る
        // （世代によってはルートのレイヤーにだけパスが付かないことがあるため）。
        // パスが全く無いファイル（グループ導入前）では全てルートとして平らに扱う。
        var roots = new List<LayerRecord>();
        var children = new Dictionary<LayerRecord, List<LayerRecord>>();
        if (kept.Any(r => r.ItemPath != null))
        {
            int rootIndex = 0;
            foreach (var r in kept) r.FullPath = r.ItemPath ?? [rootIndex++];
            var byPath = new Dictionary<string, LayerRecord>();
            foreach (var r in kept) byPath[string.Join(",", r.FullPath)] = r;
            foreach (var r in kept.OrderBy(r => r.FullPath.Length))
            {
                if (r.FullPath.Length <= 1) { roots.Add(r); continue; }
                var parentKey = string.Join(",", r.FullPath[..^1]);
                if (byPath.TryGetValue(parentKey, out var parent))
                {
                    if (!children.TryGetValue(parent, out var list)) children[parent] = list = [];
                    list.Add(r);
                }
                else roots.Add(r); // 親が見つからないものはルート扱い（壊れたパスへの保険）
            }
            var cmp = Comparer<LayerRecord>.Create((a, b) => a.FullPath[^1].CompareTo(b.FullPath[^1]));
            roots.Sort(cmp);
            foreach (var list in children.Values) list.Sort(cmp);
        }
        else roots.AddRange(kept);

        void Emit(List<LayerRecord> siblingsTopFirst)
        {
            for (int i = siblingsTopFirst.Count - 1; i >= 0; i--) // 下から順に
            {
                var r = siblingsTopFirst[i];
                if (r.IsGroup && children.TryGetValue(r, out var kids))
                {
                    Layers.Add(new PsdLayer { Kind = PsdLayerKind.FolderEnd, Name = "", Index = Layers.Count });
                    Emit(kids);
                }
                r.Layer.Index = Layers.Count;
                Layers.Add(r.Layer);
                _raw[r.Layer] = r.Raw;
                _info[r.Layer] = r.Info;
            }
        }
        Emit(roots);
        BuildTree();
    }

    /// <summary>フォルダの入れ子とパス名を組み立てる（PsdFile.BuildTree と同じ規則）。</summary>
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
            var path = l.BasePath;
            for (int n = 2; usedPaths.Contains(path); n++) path = $"{l.BasePath} #{n}";
            usedPaths.Add(path);
            l.Path = path;
            if (l.Kind == PsdLayerKind.FolderOpen) stack.Push(l);
        }
    }

    // ================= 画素 =================

    /// <summary>レイヤーの画像（マスク適用済み、不透明度は掛けていない）。画像がなければ null。</summary>
    public PsdPixels? GetPixels(PsdLayer layer)
    {
        if (!_raw.TryGetValue(layer, out var raw) || raw.Hierarchy == 0 || raw.Width == 0 || raw.Height == 0) return null;
        if (_cache.TryGetValue(layer, out var cached)) return cached;
        var bgra = DecodeHierarchy(raw.Hierarchy, raw.Width, raw.Height, Components(raw.Type));
        if (bgra == null) return null;
        if (raw.MaskHierarchy != 0 && raw.ApplyMask && !layer.Bounds.IsEmpty)
        {
            var plane = DecodeHierarchy(raw.MaskHierarchy, raw.Width, raw.Height, 1);
            if (plane != null)
                for (int i = 0; i < raw.Width * raw.Height; i++)
                {
                    int a = i * 4 + 3;
                    bgra[a] = (byte)(bgra[a] * plane[i * 4 + 2] / 255); // グレーの値は B 成分に入っている
                }
        }
        var px = new PsdPixels(layer.Bounds, bgra);
        _cache[layer] = px;
        return px;
    }

    /// <summary>レイヤーのマスク。ない・適用しない設定なら null。</summary>
    public PsdMask? GetMask(PsdLayer layer)
    {
        if (_masks.TryGetValue(layer, out var m)) return m;
        m = null;
        if (_raw.TryGetValue(layer, out var raw) && raw.MaskHierarchy != 0 && raw.ApplyMask && layer.HasMask)
        {
            var plane = DecodeHierarchy(raw.MaskHierarchy, raw.Width, raw.Height, 1);
            if (plane != null)
            {
                var gray = new byte[raw.Width * raw.Height];
                for (int i = 0; i < gray.Length; i++) gray[i] = plane[i * 4 + 2];
                m = new PsdMask(layer.MaskBounds, gray, layer.MaskDefault);
            }
        }
        _masks[layer] = m;
        return m;
    }

    private static int Components(int layerType) => layerType switch { 0 => 3, 1 => 4, 2 => 1, 3 => 2, _ => 4 };

    /// <summary>階層構造をたどって BGRA（レイヤー範囲ぶん）を返す。グレーは RGB に広げる。</summary>
    private byte[]? DecodeHierarchy(long offset, int w, int h, int components)
    {
        if (offset <= 0 || offset >= _fs.Length) return null;
        _fs.Position = offset;
        int hw = I32(), hh = I32(), bpp = I32();
        if (hw != w || hh != h || bpp < 1 || bpp > 4) return null;
        long levelPtr = Ptr();
        while (Ptr() != 0) { } // ダミーのレベルは読み飛ばす
        if (levelPtr <= 0 || levelPtr >= _fs.Length) return null;
        _fs.Position = levelPtr;
        int lw = I32(), lh = I32();
        if (lw != w || lh != h) return null;
        int cols = (w + 63) / 64, rows = (h + 63) / 64;
        var tilePtrs = new long[cols * rows];
        for (int i = 0; i < tilePtrs.Length; i++) tilePtrs[i] = Ptr();

        long bytes = (long)w * h * 4;
        if (bytes > int.MaxValue) throw new InvalidDataException("レイヤーが大きすぎます");
        var bgra = new byte[bytes];
        for (int ty = 0; ty < rows; ty++)
        {
            for (int tx = 0; tx < cols; tx++)
            {
                long tp = tilePtrs[ty * cols + tx];
                if (tp == 0) continue;
                int tw = Math.Min(64, w - tx * 64), th = Math.Min(64, h - ty * 64);
                var tile = DecodeTile(tp, tw, th, bpp);
                for (int y = 0; y < th; y++)
                {
                    for (int x = 0; x < tw; x++)
                    {
                        int si = (y * tw + x) * 4, di = ((ty * 64 + y) * w + tx * 64 + x) * 4;
                        bgra[di] = tile[si];
                        bgra[di + 1] = tile[si + 1];
                        bgra[di + 2] = tile[si + 2];
                        bgra[di + 3] = tile[si + 3];
                    }
                }
            }
        }
        return bgra;
    }

    /// <summary>タイル 1 つを BGRA（tw×th×4、成分は平面／混在どちらの並びからも起こす）に展開する。</summary>
    private byte[] DecodeTile(long offset, int tw, int th, int bpp)
    {
        int count = tw * th;
        var tile = new byte[count * 4];
        _fs.Position = offset;
        if (_compression == 1)
        {
            // RLE: 成分ごとに stream が分かれている
            var plane = new byte[count];
            for (int c = 0; c < bpp; c++)
            {
                RleDecode(plane);
                for (int i = 0; i < count; i++) SetComponent(tile, i, c, bpp, plane[i]);
            }
        }
        else
        {
            var raw = new byte[count * bpp];
            if (_compression == 0)
            {
                Read(raw);
            }
            else // zlib: ストリームは自己終端なので、必要な分だけ解凍して読む
            {
                using var z = new ZLibStream(_fs, CompressionMode.Decompress, leaveOpen: true);
                int n = 0, r;
                var buf = new byte[8192];
                while (n < raw.Length && (r = z.Read(buf, 0, Math.Min(buf.Length, raw.Length - n))) > 0)
                {
                    Array.Copy(buf, 0, raw, n, r);
                    n += r;
                }
                if (n < raw.Length) throw new InvalidDataException("zlib データが途中で終わっています");
            }
            for (int i = 0; i < count; i++)
                for (int c = 0; c < bpp; c++)
                    SetComponent(tile, i, c, bpp, raw[i * bpp + c]);
        }
        if (bpp is 1 or 3) // α が無いタイルは不透明にする（グレーは SetComponent で RGB へ広げ済み）
            for (int i = 3; i < tile.Length; i += 4) tile[i] = 255;
        return tile;
    }

    /// <summary>成分 c の値を BGRA に置く。bpp 1/2 はグレー（＋α）、3/4 は RGB（＋α）。</summary>
    private static void SetComponent(byte[] tile, int i, int c, int bpp, byte v)
    {
        int o = i * 4;
        if (bpp <= 2)
        {
            if (c == 0) { tile[o] = v; tile[o + 1] = v; tile[o + 2] = v; }
            else tile[o + 3] = v;
        }
        else
        {
            if (c == 0) tile[o + 2] = v;      // R
            else if (c == 1) tile[o + 1] = v; // G
            else if (c == 2) tile[o] = v;     // B
            else tile[o + 3] = v;             // A
        }
    }

    /// <summary>RLE の stream 1 成分ぶんを dst に展開する（GIMP の PackBits 変種）。</summary>
    private void RleDecode(byte[] dst)
    {
        int i = 0;
        while (i < dst.Length)
        {
            int n = _fs.ReadByte();
            if (n < 0) throw new InvalidDataException("RLE データが途中で終わっています");
            if (n <= 126)
            {
                int v = _fs.ReadByte();
                if (v < 0) throw new InvalidDataException("RLE データが途中で終わっています");
                int len = Math.Min(n + 1, dst.Length - i);
                for (int k = 0; k < len; k++) dst[i++] = (byte)v;
            }
            else if (n == 127)
            {
                int len = U16FromStream();
                int v = _fs.ReadByte();
                if (v < 0) throw new InvalidDataException("RLE データが途中で終わっています");
                len = Math.Min(len, dst.Length - i);
                for (int k = 0; k < len; k++) dst[i++] = (byte)v;
            }
            else if (n == 128)
            {
                int len = Math.Min(U16FromStream(), dst.Length - i);
                Read(dst.AsSpan(i, len));
                i += len;
            }
            else
            {
                int len = Math.Min(256 - n, dst.Length - i);
                Read(dst.AsSpan(i, len));
                i += len;
            }
        }
    }

    private int U16FromStream()
    {
        int hi = _fs.ReadByte(), lo = _fs.ReadByte();
        if (hi < 0 || lo < 0) throw new InvalidDataException("RLE データが途中で終わっています");
        return hi << 8 | lo;
    }

    // ================= 描画モード =================

    /// <summary>
    /// GIMP の描画モード番号を PSD のキーへ写す。写せないものは通常（norm）にして unsupported を立てる。
    /// 番号は GimpLayerMode（0〜61）。レガシー（0〜22）と標準（23〜61）で同じ意味のものは同じキーにする。
    /// </summary>
    public static string MapBlendMode(int mode, out bool approx, out bool unsupported)
    {
        approx = false;
        unsupported = false;
        switch (mode)
        {
            case 0: case 28: return "norm";                    // 通常
            case 1: return "diss";                             // ディザ合成（PSD 側で通常として重ね、△を出す）
            case 3: case 30: return "mul ";                    // 乗算
            case 4: case 31: return "scrn";                   // スクリーン
            case 5: case 23: return "over";                    // オーバーレイ
            case 6: case 32: return "diff";                    // 差の絶対値
            case 7: case 33: return "lddg";                    // 加算
            case 8: case 34: return "fsub";                    // 減算
            case 9: case 35: return "dark";                    // 比較（暗）
            case 10: case 36: return "lite";                   // 比較（明）
            case 11: case 37: return "hue ";                   // 色相（HSV）
            case 12: case 38: return "sat ";                   // 彩度（HSV）
            case 13: case 39: return "colr";                   // カラー（HSL）
            case 15: case 41: return "fdiv";                   // 除算
            case 16: case 42: return "div ";                   // 覆い焼きカラー
            case 17: case 43: return "idiv";                   // 焼き込みカラー
            case 18: case 44: return "hLit";                   // ハードライト
            case 19: case 45: return "sLit";                   // ソフトライト
            case 48: return "vLit";                            // ビビッドライト
            case 49: return "pLit";                            // ピンライト
            case 50: return "lLit";                            // リニアライト
            case 51: return "hMix";                            // ハードミックス
            case 52: return "smud";                            // 除外
            case 53: return "lbrn";                            // 焼き込み（リニア）
            case 61: return "pass";                            // 通過（グループ用）
            // ここからは近似（色空間の定義が PSD と違う）
            case 14: case 40: approx = true; return "lum ";    // 明度（HSV の V）→ 輝度で近似
            case 24: approx = true; return "hue ";             // 色相（LCh）
            case 26: approx = true; return "colr";             // カラー（LCh）
            case 27: approx = true; return "lum ";             // 明度（LCh）
            case 54: approx = true; return "dark";             // 比較（暗・輝度）
            case 55: approx = true; return "lite";             // 比較（明・輝度）
            case 56: approx = true; return "lum ";             // 輝度（LCh）
            // 写せないもの
            default: unsupported = true; return "norm";
        }
    }

    /// <summary>GIMP の描画モード番号の日本語名（写せないものの表示用）。</summary>
    public static string ModeName(int mode) => mode switch
    {
        2 or 29 => "背後",
        20 or 46 => "グレイン抽出",
        21 or 47 => "グレイン統合",
        22 or 57 => "カラー消去",
        25 => "クロマ（LCh）",
        58 => "消去",
        59 => "統合（置き換え）",
        60 => "分割（反消去）",
        _ => $"モード {mode}",
    };
}
