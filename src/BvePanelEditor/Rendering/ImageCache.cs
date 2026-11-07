using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BvePanelEditor.Rendering;

/// <summary>パネル用画像の読み込み（透過色・色の掛け合わせ済み）。ファイルはロックしない。</summary>
public sealed class ImageCache
{
    private readonly Dictionary<string, BitmapSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public void Clear() => _cache.Clear();

    /// <param name="transparent">透明にする色（RGB）。null なら無し。</param>
    /// <param name="tint">掛け合わせる色（RGB）。null なら無し。</param>
    public BitmapSource? Get(string fullPath, (byte R, byte G, byte B)? transparent, (byte R, byte G, byte B)? tint)
    {
        string key = $"{fullPath}|{transparent}|{tint}";
        if (_cache.TryGetValue(key, out var hit)) return hit;
        var img = Load(fullPath, transparent, tint);
        _cache[key] = img;
        return img;
    }

    private static BitmapSource? Load(string path, (byte R, byte G, byte B)? transparent, (byte R, byte G, byte B)? tint)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.StreamSource = new MemoryStream(bytes);
            bi.EndInit();
            bi.Freeze();

            BitmapSource src = bi;
            if (transparent == null && tint == null && bi.Format == PixelFormats.Bgra32)
                return bi;

            var conv = new FormatConvertedBitmap(bi, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            conv.CopyPixels(px, stride, 0);
            for (int i = 0; i < px.Length; i += 4)
            {
                if (transparent is { } t && px[i] == t.B && px[i + 1] == t.G && px[i + 2] == t.R)
                {
                    px[i + 3] = 0;
                    continue;
                }
                if (tint is { } c)
                {
                    px[i] = (byte)(px[i] * c.B / 255);
                    px[i + 1] = (byte)(px[i + 1] * c.G / 255);
                    px[i + 2] = (byte)(px[i + 2] * c.R / 255);
                }
            }
            src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
            src.Freeze();
            return src;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
