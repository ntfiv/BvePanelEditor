using System.IO;
using System.Windows;
using BvePanelEditor.Core;
using BvePanelEditor.Core.Crop;
using BvePanelEditor.Core.Psd;
using BvePanelEditor.Rendering;
using BvePanelEditor.Views;
using Microsoft.Win32;

namespace BvePanelEditor;

/// <summary>指針の蓋を作る（パネル本体の画像や PSD のレイヤーから、針の軸の蓋を切り抜いて針の上に置く）。</summary>
public partial class MainWindow
{
    /// <summary>今選んでいる指針と、それと同じ位置の指針（蓋をまとめてかぶせる）。指針を選んでいなければ空。</summary>
    private List<int> CoverTargets()
    {
        if (_doc == null) return [];
        // グループごと選んでいるときは、その中身（組で置いた針など）
        var sel = _selGroup != null ? _doc.Members(_selGroup)
            : _selSet.Count > 0 ? _selSet : _selected >= 0 ? [_selected] : [];
        if (sel.Count == 0 || sel.Any(i => i < 0 || i >= _doc.Sections.Count || _doc.Sections[i].Kind != SectionKind.Needle)) return [];
        if (!PanelValues.TryPoint(_doc.Sections[sel[0]].Get("Location"), out var x, out var y)) return [];
        var at = new List<int>();
        for (int i = 0; i < _doc.Sections.Count; i++)
            if (_doc.Sections[i].Kind == SectionKind.Needle && PanelValues.TryPoint(_doc.Sections[i].Get("Location"), out var nx, out var ny)
                && Math.Abs(nx - x) <= 1 && Math.Abs(ny - y) <= 1)
                at.Add(i);
        return at;
    }

    /// <summary>左上の「蓋を作る」ボタンを、指針を選んでいるときだけ出す。</summary>
    private void UpdateCoverButton()
    {
        CoverButton.Visibility = !_variationMode && CoverTargets().Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnMakeCover(object? sender, RoutedEventArgs? e)
    {
        if (_doc == null) return;
        FlushReparse();
        var targets = CoverTargets();
        if (targets.Count == 0) { Message("蓋を作る指針を選んでください"); return; }
        if (PanelDir == null) { Warn("先にパネルファイルを保存してください（蓋の画像をパネルのフォルダに書き出すため）。"); return; }
        var sources = CoverSources(targets);
        if (sources.Count == 0) { Warn("針の下にある画像（[This] の画像や、常表示の表示灯）が見つかりません。"); return; }
        var needles = targets.Select(i => _scene?.ItemFor(i)).OfType<RenderItem>().ToList();
        var w = new CoverWindow(this, sources, needles, DefaultCoverName(targets), PickCoverPsd);
        if (w.ShowDialog() != true || w.Result == null) return;
        PlaceCover(w.Result, targets);
    }

    private string DefaultCoverName(List<int> targets)
    {
        var titles = targets.Select(i => _doc!.Sections[i].Title).Where(t => t.Length > 0).Distinct().ToList();
        // ファイル名にもなるので、スペースは入れない（「cover_速度計」）
        return titles.Count > 0 ? CoverFileName($"cover_{string.Join("_", titles)}") : "cover";
    }

    /// <summary>蓋の画像のファイル名（使えない文字を外し、スペースは「_」にする）。</summary>
    public static string CoverFileName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(PsdImport.SafeFileName(name.Trim()), @"[\s　]+", "_");

    private string FullOf(string rel) => Path.GetFullPath(Path.Combine(PanelDir!, rel.Trim().Trim('"')));

    /// <summary>
    /// 切り抜く元の候補。針より下にある、常表示の表示灯（Subject = true）のうち針の軸の所に絵があるものを上から順に、最後に [This]。
    /// </summary>
    public List<CoverSource> CoverSources(List<int> targets)
    {
        var doc = _doc!;
        var list = new List<(int Layer, int Index, CoverSource Src)>();
        if (!PanelValues.TryPoint(doc.Sections[targets[0]].Get("Location"), out var nx, out var ny)) return [];
        int needleLayer = targets.Min(i => (int)PanelValues.Double(doc.Sections[i].Get("Layer"), 0));
        for (int i = 0; i < doc.Sections.Count; i++)
        {
            var s = doc.Sections[i];
            if (s.Kind != SectionKind.PilotLamp || !string.Equals(s.Get("Subject")?.Trim(), "true", StringComparison.OrdinalIgnoreCase)) continue;
            int layer = (int)PanelValues.Double(s.Get("Layer"), 0);
            if (layer >= needleLayer || s.Get("DaytimeImage") is not { Length: > 0 } day) continue;
            PanelValues.TryPoint(s.Get("Location"), out var lx, out var ly);
            var src = new CoverSource
            {
                Label = $"{(s.Title.Length > 0 ? s.Title : "表示灯")}（{PathDisplay.Show(day.Trim())}・レイヤー {layer}）",
                DayPath = FullOf(day),
                NightPath = s.Get("NighttimeImage") is { Length: > 0 } night ? FullOf(night) : null,
                X = (int)Math.Round(lx), Y = (int)Math.Round(ly),
                LinkSet = s.CropSet,
            };
            // 針の軸の所に絵がある（透明でない）ものだけ
            if (!src.EnsureLoaded() || src.Day is not { } px) continue;
            int px0 = (int)Math.Round(nx) - src.X, py0 = (int)Math.Round(ny) - src.Y;
            if (px0 < 0 || py0 < 0 || px0 >= px.Width || py0 >= px.Height || px.Bgra[(py0 * px.Width + px0) * 4 + 3] < 32) continue;
            list.Add((layer, i, src));
        }
        var result = list.OrderByDescending(t => t.Layer).ThenByDescending(t => t.Index).Select(t => t.Src).ToList();
        // 透明な [This]（メインパネルの形）は切り抜く元にしない
        if (doc.This is { } self && self.Get("DaytimeImage") is { Length: > 0 } td && !IsBlankThis())
        {
            var src = new CoverSource
            {
                Label = $"[This]（{PathDisplay.Show(td.Trim())}）",
                DayPath = FullOf(td),
                NightPath = self.Get("NighttimeImage") is { Length: > 0 } tn ? FullOf(tn) : null,
                LinkSet = doc.CropSets.Any(c => c.Name == CropSetNames.Base) ? CropSetNames.Base : null,
            };
            if (src.EnsureLoaded()) result.Add(src);
        }
        return result;
    }

    /// <summary>PSD とレイヤー（昼・夜）を選ばせて、切り抜く元にする。</summary>
    private CoverSource? PickCoverPsd()
    {
        var known = _doc?.CropSets.Where(c => c.HasLayers).Select(c => FullOf(c.Day)).FirstOrDefault(File.Exists)
            ?? _doc?.PsdSources.Select(p => p.Day).Where(d => d.Length > 0).Select(FullOf).FirstOrDefault(File.Exists);
        var dlg = new OpenFileDialog { Filter = "PSD (*.psd)|*.psd", Title = "蓋のレイヤーがある PSD を選ぶ" };
        if (known != null) { dlg.InitialDirectory = Path.GetDirectoryName(known); dlg.FileName = Path.GetFileName(known); }
        else if (PanelDir != null) dlg.InitialDirectory = PanelDir;
        if (dlg.ShowDialog(this) != true) return null;
        return PickCoverPsdLayers(dlg.FileName);
    }

    private CoverSource? PickCoverPsdLayers(string psd)
    {
        var aw = new LayerAssignWindow(this, [psd], ["昼の画像", "夜の画像"], [new(false, false), new(true, false)], [psd, psd], [null, null], "指針の蓋");
        if (aw.ShowDialog() != true || aw.Result?[0] is not { } day) return null;
        var night = aw.Result[1];
        return new CoverSource
        {
            Label = $"PSD のレイヤー（{Path.GetFileName(day.PsdPath)}: {string.Join("・", day.Layers.Select(l => l.Split('/')[^1]))}）",
            DayPath = day.PsdPath, DayLayers = day.Layers, Day = day.Pixels,
            NightPath = night?.PsdPath, NightLayers = night?.Layers, Night = night?.Pixels,
        };
    }

    /// <summary>蓋を書き出し、常表示の表示灯として針のすぐ上に置く。置いた計器の番号を返す（できなければ -1）。</summary>
    public int PlaceCover(CoverResult r, List<int> targets)
    {
        if (_doc == null || PanelDir == null) return -1;
        FlushReparse();
        var rect = CropShape.Bounds(r.Shapes);
        var day = r.Source.Cut(false, r.Shapes);
        if (day == null || rect.IsEmpty) { Warn("切り抜く形がありません"); return -1; }
        var night = r.Source.Night != null ? r.Source.Cut(true, r.Shapes) : null;

        // 書き出す名前（crop フォルダの中で、昼「名前.png」と夜「名前_N.png」がどちらもまだないもの）
        var dir = Path.Combine(PanelDir, "crop");
        var baseName = CoverFileName(r.Name);
        var name = baseName;
        for (int i = 2; File.Exists(Path.Combine(dir, name + ".png")) || File.Exists(Path.Combine(dir, name + "_N.png")); i++) name = $"{baseName}_{i}";
        var dayFile = Path.Combine(dir, name + ".png");
        var nightFile = Path.Combine(dir, name + "_N.png");
        try
        {
            WriteNewFile(dayFile, PngWriter.EncodePow2(day));
            if (night != null) WriteNewFile(nightFile, PngWriter.EncodePow2(night));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"蓋の画像を書き出せませんでした:\n{ex.Message}");
            return -1;
        }

        var sec = PanelSection.Create(SectionKind.PilotLamp);
        sec.SetTitle(r.Name);
        sec.Set("Subject", "true");
        sec.Set("Location", PanelValues.Point(rect.Left, rect.Top));
        sec.Set("DaytimeImage", PropertyEditor.RelativePath(PanelDir, dayFile));
        if (night != null) sec.Set("NighttimeImage", PropertyEditor.RelativePath(PanelDir, nightFile));
        int layer = targets.Max(i => (int)PanelValues.Double(_doc.Sections[i].Get("Layer"), 0)) + 1;
        sec.Set("Layer", layer.ToString());

        // 切り抜き元とつなぐ（「切り抜き元から更新」で、形のまま切り抜き直せるように）
        string? note = null;
        sec.CropSet = CoverLinkSet(r, ref note);
        if (sec.CropSet != null) sec.CropShapes.AddRange(r.Shapes.Select(s => s.Offset(-rect.Left, -rect.Top)));

        int at = targets.Max() + 1;
        sec.SetGroups(_doc.Sections[targets.Max()].Groups); // 針と同じグループに入れる
        sec.VariationCond = _doc.Sections[targets.Max()].VariationCond; // 針と同じバリエーションに入れる
        _doc.Sections.Insert(at, sec);
        CommitModel();
        _cache.Clear();
        RebuildAll();
        ApplySelection(at, [at], null, SelectionSource.Other);
        Canvas.AllowFreshEdit([at]);
        Canvas.Focus();
        Message($"蓋「{r.Name}」を置きました（レイヤー {layer}・{PropertyEditor.RelativePath(PanelDir, dayFile)}）。"
            + (note ?? "") + "選んでいる間は位置ロック中でも動かせます");
        return at;
    }

    /// <summary>蓋の切り抜き元の組の名前。つなげないとき（パネルの左上に置いていない画像）は null と理由。</summary>
    private string? CoverLinkSet(CoverResult r, ref string? note)
    {
        var doc = _doc!;
        var src = r.Source;
        if (src.LinkSet != null && doc.CropSets.FirstOrDefault(c => c.Name == src.LinkSet) is { } linked)
        {
            if (linked.Name != CropSetNames.Base) return linked.Name;
            // 「本体」を計器が指すと、切り抜き元から更新で [This] を作り直さなくなる（本体を表示灯として置いた扱いになる）ので、同じ中身の組を別の名前で使う
            static bool Same(List<string>? a, List<string>? b) => a == null ? b == null : b != null && a.SequenceEqual(b);
            var copy = doc.CropSets.FirstOrDefault(c => c.Name != CropSetNames.Base && c.Day == linked.Day && c.Night == linked.Night
                && Same(c.DayLayers, linked.DayLayers) && Same(c.NightLayers, linked.NightLayers));
            if (copy != null) return copy.Name;
            copy = new CropSet
            {
                Name = UniqueSetName("本体の絵"), Day = linked.Day, Night = linked.Night,
                DayLayers = linked.DayLayers != null ? [.. linked.DayLayers] : null,
                NightLayers = linked.NightLayers != null ? [.. linked.NightLayers] : null,
            };
            doc.CropSets.Add(copy);
            return copy.Name;
        }
        string Rel(string full) => PsdImport.PathFrom(PanelDir!, full);
        if (src.IsPsd)
        {
            var set = new CropSet
            {
                Name = UniqueSetName($"{CoverFileName(r.Name)}"),
                Day = Rel(src.DayPath), DayLayers = [.. src.DayLayers!],
                Night = src.NightPath != null ? Rel(src.NightPath) : null,
                NightLayers = src.NightPath != null && src.NightLayers != null ? [.. src.NightLayers] : null,
            };
            doc.CropSets.Add(set);
            return set.Name;
        }
        if (src.X != 0 || src.Y != 0)
        {
            note = "切り抜いた元の画像がパネルの左上（0, 0）にないので、「切り抜き元から更新」とはつなぎませんでした。";
            return null;
        }
        var day = Rel(src.DayPath);
        var nightRel = src.NightPath != null ? Rel(src.NightPath) : null;
        var same = doc.CropSets.FirstOrDefault(c => !c.HasLayers && string.Equals(c.Day, day, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.Night, nightRel, StringComparison.OrdinalIgnoreCase));
        if (same != null) return same.Name;
        var added = new CropSet { Name = UniqueSetName($"元 {Path.GetFileNameWithoutExtension(src.DayPath)}"), Day = day, Night = nightRel };
        doc.CropSets.Add(added);
        return added.Name;
    }

    private string UniqueSetName(string name)
    {
        var n = name;
        for (int i = 2; _doc!.CropSets.Any(c => c.Name == n); i++) n = $"{name} {i}";
        return n;
    }
}
