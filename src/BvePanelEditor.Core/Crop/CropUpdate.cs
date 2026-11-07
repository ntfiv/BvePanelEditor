using BvePanelEditor.Core.Psd;

namespace BvePanelEditor.Core.Crop;

/// <summary>「切り抜き元から更新」: 今の位置と画像の大きさの範囲で、切り抜き元から切り出し直す。</summary>
public static class CropUpdate
{
    /// <param name="load">フルパスから画像を読む（読めなければ null）。PSD のレイヤーを重ねるときは使わない。</param>
    public static PsdUpdatePlan Plan(PanelDocument doc, string panelDir, Func<string, PsdPixels?> load)
    {
        var plan = new PsdUpdatePlan();
        var cache = new Dictionary<string, PsdPixels?>(StringComparer.OrdinalIgnoreCase);
        var psds = new Dictionary<string, PsdFile?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // 切り抜き元の絵。レイヤーがあれば PSD から重ね、なければ画像として読む
            PsdPixels? Source(string full, List<string>? layers, string label)
            {
                var key = layers == null ? full : full + "\n" + string.Join("\n", layers);
                if (cache.TryGetValue(key, out var img)) return img;
                if (layers == null) img = load(full); // ファイルがないときは load が null を返す
                else
                {
                    if (!psds.TryGetValue(full, out var psd))
                    {
                        try { psd = File.Exists(full) ? PsdFile.Open(full) : null; }
                        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
                        {
                            psd = null;
                            plan.Warnings.Add($"PSD「{Path.GetFileName(full)}」を読めません: {e.Message}");
                        }
                        psds[full] = psd;
                    }
                    if (psd != null)
                    {
                        var missing = new List<string>();
                        var warn = new List<string>();
                        img = CropLayers.Compose(psd, layers, warn, missing);
                        foreach (var m in missing) plan.Warnings.Add($"{label}: レイヤー「{m}」が「{Path.GetFileName(full)}」にありません");
                        foreach (var w in warn) plan.Warnings.Add($"{Path.GetFileName(full)}: {w}");
                    }
                }
                cache[key] = img;
                return img;
            }
            string? Full(string? rel) => string.IsNullOrWhiteSpace(rel) ? null : Path.GetFullPath(Path.Combine(panelDir, rel.Trim().Trim('"')));
            // 「メインパネルに重ねる」切り抜き元: 透明な所へメインパネルの画像を重ねる
            var main = doc.Sections.FirstOrDefault(s => s.IsMainPanel);
            PsdPixels OverMain(PsdPixels src, bool night, string label)
            {
                var path = main == null ? null : Full(night ? main.Get("NighttimeImage") ?? main.Get("DaytimeImage") : main.Get("DaytimeImage"));
                PsdPixels? under = null;
                if (path != null && !cache.TryGetValue(path, out under)) cache[path] = under = load(path);
                if (under == null) { plan.Warnings.Add($"{label}: メインパネルの画像が読めないので、重ねずに切り抜きました"); return src; }
                PanelValues.TryPoint(main!.Get("Location"), out var mx, out var my);
                return CropCompose.Over(under, src, (int)mx, (int)my);
            }

            // パネル本体（PSD のレイヤーから重ねたもの）
            // パネル本体を表示灯として置いているとき（下に敷く画像がある）は、その表示灯として下で作り直す
            var baseSet = doc.CropSets.FirstOrDefault(c => c.Name == CropSetNames.Base && c.HasLayers);
            if (doc.Sections.Any(s => s.CropSet == CropSetNames.Base)) baseSet = null;
            if (baseSet != null && doc.This is { } self)
            {
                plan.Linked++;
                var targets = new List<(string Image, string Source, List<string> Layers, string Label)>();
                if (baseSet.DayLayers != null && Full(self.Get("DaytimeImage")) is { } d && Full(baseSet.Day) is { } ds)
                    targets.Add((d, ds, baseSet.DayLayers, "パネル本体（昼）"));
                if (baseSet.NightLayers != null && Full(self.Get("NighttimeImage")) is { } n && Full(baseSet.Night) is { } ns)
                    targets.Add((n, ns, baseSet.NightLayers, "パネル本体（夜）"));
                foreach (var (image, source, layers, label) in targets)
                {
                    if (File.Exists(image) && !PngWriter.IsPanelKunPng(image)) { plan.Skipped.Add($"{label}: {Path.GetFileName(image)}"); continue; }
                    var src = Source(source, layers, label);
                    if (src == null) { plan.Missing.Add($"{label}: 切り抜き元の PSD「{Path.GetFileName(source)}」がありません"); continue; }
                    plan.Files.Add(new(image, PngWriter.EncodePow2(src)));
                }
            }

            for (int i = 0; i < doc.Sections.Count; i++)
            {
                var s = doc.Sections[i];
                if (s.CropSet == null) continue;
                plan.Linked++;
                var label = $"#{i + 1} {(s.Title.Length > 0 ? s.Title : PanelSpec.KindLabel(s.Kind))}";
                var set = doc.CropSets.FirstOrDefault(c => c.Name == s.CropSet);
                if (set == null) { plan.Missing.Add($"{label}: 切り抜き元「{s.CropSet}」がパネルに書かれていません"); continue; }
                if (!PanelValues.TryPoint(s.Get("Location"), out var lx, out var ly)) { plan.Missing.Add($"{label}: Location がありません"); continue; }

                var targets = new List<(string Image, string? Source, List<string>? Layers, bool Night)>();
                if (Full(s.Get("DaytimeImage")) is { } dayImg) targets.Add((dayImg, Full(set.Day), set.DayLayers, false));
                if (Full(s.Get("NighttimeImage")) is { } nightImg && set.Night != null) targets.Add((nightImg, Full(set.Night), set.NightLayers, true));
                var foreign = targets.Where(t => File.Exists(t.Image) && !PngWriter.IsPanelKunPng(t.Image)).Select(t => Path.GetFileName(t.Image)).ToList();
                if (foreign.Count > 0) { plan.Skipped.Add($"{label}: {string.Join("、", foreign)}"); continue; }

                foreach (var (image, source, layers, night) in targets)
                {
                    if (!PngWriter.TryReadContentSize(image, out var w, out var h)) { plan.Missing.Add($"{label}: 画像「{Path.GetFileName(image)}」が読めません"); continue; }
                    var src = source != null ? Source(source, layers, label) : null;
                    if (src == null) { plan.Missing.Add($"{label}: 切り抜き元の画像「{source}」がありません"); continue; }
                    if (set.OverMain) src = OverMain(src, night, label);
                    var rect = new PsdRect((int)lx, (int)ly, (int)lx + w, (int)ly + h);
                    if (rect.Intersect(src.Bounds) != rect) plan.Warnings.Add($"{label}: 範囲が切り抜き元の画像からはみ出しています（はみ出した所は透明になります）");
                    // 形（丸・多角形）があれば、形の外を透明にする
                    plan.Files.Add(new(image, PngWriter.EncodePow2(CropShape.Mask(RegionDetect.CropOf(src, rect), s.CropShapes))));
                }
            }
        }
        finally
        {
            foreach (var p in psds.Values) p?.Dispose();
        }
        var distinct = plan.Warnings.Distinct().ToList();
        plan.Warnings.Clear();
        plan.Warnings.AddRange(distinct);
        return plan;
    }
}
