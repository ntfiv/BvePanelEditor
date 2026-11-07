namespace BvePanelEditor.Core.Crop;

/// <summary>
/// ある画像（PSD）から作ったパネルファイルを探す。画像を開いたとき、作り直して上書きしてしまわないように、
/// 「このパネルを開きますか」と聞くために使う。
/// </summary>
public static class CropPanelFinder
{
    /// <summary>パネルが、この画像を切り抜き元・パネル本体として使っているか。</summary>
    public static bool References(PanelDocument doc, string panelDir, string imageFull)
    {
        bool Same(string? rel) => !string.IsNullOrWhiteSpace(rel)
            && string.Equals(Path.GetFullPath(Path.Combine(panelDir, rel.Trim().Trim('"'))), imageFull, StringComparison.OrdinalIgnoreCase);
        if (doc.CropSets.Any(c => Same(c.Day) || Same(c.Night))) return true;
        // パネル本体は、メインパネルの表示灯（[This] が透明な形）か [This] の画像
        if (doc.MainPanel is { } m && (Same(m.Get("DaytimeImage")) || Same(m.Get("NighttimeImage")))) return true;
        return doc.This is { } t && (Same(t.Get("DaytimeImage")) || Same(t.Get("NighttimeImage")));
    }

    /// <summary>
    /// 画像を使っているパネルファイルを探す。探す場所は extra（覚えているパネル）と、画像のフォルダ・その親・画像のフォルダの 1 つ下のフォルダ。
    /// </summary>
    public static List<string> Find(string imagePath, IEnumerable<string> extra)
    {
        var image = Path.GetFullPath(imagePath);
        var dir = Path.GetDirectoryName(image)!;
        var candidates = new List<string>(extra);
        void Scan(string? d)
        {
            if (d == null || !Directory.Exists(d)) return;
            try
            {
                candidates.AddRange(Directory.EnumerateFiles(d).Where(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)).Take(300));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        Scan(dir);
        Scan(Path.GetDirectoryName(dir));
        try { foreach (var sub in Directory.EnumerateDirectories(dir).Take(50)) Scan(sub); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        var found = new List<string>();
        foreach (var f in candidates.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var info = new FileInfo(f);
                if (!info.Exists || info.Length > 4 * 1024 * 1024) continue;
                var (text, _) = BveText.Read(f);
                if (BveFiles.Detect(text) != BveFileKind.Panel) continue;
                if (References(PanelDocument.Parse(text), Path.GetDirectoryName(f)!, image)) found.Add(f);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        // 新しく保存したものを先に
        return found.OrderByDescending(File.GetLastWriteTimeUtc).ToList();
    }
}
