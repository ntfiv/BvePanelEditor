using System.Text;

namespace BvePanelEditor.Core.Psd;

/// <summary>
/// PSD の「記述子（Descriptor）」を読む。塗りつぶしレイヤーの色や、明るさ・コントラストの値などが入っている。
/// 値は キー → (double / int / long / bool / string / Dictionary / List) の形にする。読めない型があれば null を返す。
/// </summary>
public static class PsdDescriptor
{
    /// <summary>先頭 4 バイトの版を飛ばしてから記述子を読む。</summary>
    public static Dictionary<string, object>? ReadVersioned(byte[] data)
    {
        try
        {
            var r = new Reader(data);
            r.U32();
            return r.Descriptor();
        }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException) { return null; }
    }

    public static double? Number(Dictionary<string, object> d, string key) => d.TryGetValue(key, out var v) ? v switch
    {
        double x => x, int x => x, long x => x, _ => null,
    } : null;

    private sealed class Reader(byte[] data)
    {
        private int _p;

        private void Need(int n) { if (_p + n > data.Length) throw new InvalidDataException("記述子が途中で終わっています"); }
        public uint U32() { Need(4); uint v = (uint)(data[_p] << 24 | data[_p + 1] << 16 | data[_p + 2] << 8 | data[_p + 3]); _p += 4; return v; }
        private int I32() => (int)U32();
        private string Ascii(int n) { Need(n); var s = Encoding.ASCII.GetString(data, _p, n); _p += n; return s; }
        private double Double() { Need(8); var b = data.AsSpan(_p, 8).ToArray(); _p += 8; if (BitConverter.IsLittleEndian) Array.Reverse(b); return BitConverter.ToDouble(b); }

        private string Unicode()
        {
            int n = I32();
            Need(n * 2);
            var s = Encoding.BigEndianUnicode.GetString(data, _p, n * 2).TrimEnd('\0');
            _p += n * 2;
            return s;
        }

        /// <summary>長さ 0 なら 4 文字のキー、そうでなければその長さの文字列。</summary>
        private string Id()
        {
            int n = I32();
            return Ascii(n == 0 ? 4 : n);
        }

        public Dictionary<string, object> Descriptor()
        {
            Unicode();  // 名前
            Id();       // クラス
            int count = I32();
            var d = new Dictionary<string, object>();
            for (int i = 0; i < count; i++)
            {
                var key = Id();
                d[key] = Value(Ascii(4));
            }
            return d;
        }

        private object Value(string type)
        {
            switch (type)
            {
                case "Objc": case "GlbO": return Descriptor();
                case "VlLs":
                    {
                        int n = I32();
                        var list = new List<object>();
                        for (int i = 0; i < n; i++) list.Add(Value(Ascii(4)));
                        return list;
                    }
                case "doub": return Double();
                case "UntF": Ascii(4); return Double();
                case "UnFl":
                    {
                        Ascii(4);
                        int n = I32();
                        var list = new List<object>();
                        for (int i = 0; i < n; i++) list.Add(Double());
                        return list;
                    }
                case "TEXT": return Unicode();
                case "enum": Id(); return Id();
                case "long": return I32();
                case "comp": { long hi = U32(); long lo = U32(); return hi << 32 | lo; }
                case "bool": Need(1); return data[_p++] != 0;
                case "type": case "GlbC": Unicode(); return Id();
                case "alis": case "tdta": { int n = I32(); Need(n); _p += n; return n; }
                default: throw new InvalidDataException($"記述子の型「{type}」は読めません");
            }
        }
    }
}
