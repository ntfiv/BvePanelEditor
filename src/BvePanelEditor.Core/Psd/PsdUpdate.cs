namespace BvePanelEditor.Core.Psd;

/// <summary>「PSD から更新」でやること。</summary>
public sealed class PsdUpdatePlan
{
    public List<PsdOutputFile> Files = [];
    /// <summary>位置が変わるオブジェクト（番号、新しい左上）。</summary>
    public List<(int Section, int X, int Y)> Moves = [];
    /// <summary>コマの高さが変わるデジタル数字（番号、新しい高さ）。</summary>
    public List<(int Section, int Interval)> Intervals = [];
    /// <summary>PSD に見つからないレイヤー。</summary>
    public List<string> Missing = [];
    public List<string> Warnings = [];
    /// <summary>画像がパネルくんの書き出したものではない（手で差し替えた）ので、書き出しも移動もしないオブジェクト。</summary>
    public List<string> Skipped = [];
    /// <summary>つながりのあるオブジェクトの数。</summary>
    public int Linked;

    /// <summary>ほかの PSD の結果をまとめる。</summary>
    public void Merge(PsdUpdatePlan o)
    {
        Files.AddRange(o.Files);
        Moves.AddRange(o.Moves);
        Intervals.AddRange(o.Intervals);
        Missing.AddRange(o.Missing);
        Warnings.AddRange(o.Warnings.Where(w => !Warnings.Contains(w)));
        Skipped.AddRange(o.Skipped);
        Linked += o.Linked;
    }
}

/// <summary>パネルに書いた PSD とのつながりから、画像を書き出し直す。</summary>
public static class PsdUpdate
{
    /// <param name="sourceId">どの PSD のオブジェクトを更新するか（PanelSection.PsdSource）。</param>
    public static PsdUpdatePlan Plan(PanelDocument doc, string panelDir, PsdFile day, PsdFile? night, int sourceId = 1)
    {
        var plan = new PsdUpdatePlan();
        string? Full(string? rel) => string.IsNullOrWhiteSpace(rel) ? null : Path.GetFullPath(Path.Combine(panelDir, rel.Trim().Trim('"')));

        for (int i = 0; i < doc.Sections.Count; i++)
        {
            var s = doc.Sections[i];
            if (s.PsdLayers.Count == 0 || s.PsdSource != sourceId) continue;
            plan.Linked++;
            var label = $"#{i + 1} {(s.Title.Length > 0 ? s.Title : PanelSpec.KindLabel(s.Kind))}";
            // 今の画像がパネルくんの書き出したものでなければ（手で差し替えた）、上書きも移動もしない
            var foreign = new[] { Full(s.Get("DaytimeImage")), Full(s.Get("NighttimeImage")) }
                .OfType<string>().Where(f => File.Exists(f) && !PngWriter.IsPanelKunPng(f)).ToList();
            if (foreign.Count > 0)
            {
                plan.Skipped.Add($"{label}: {string.Join("、", foreign.Select(Path.GetFileName))}");
                continue;
            }
            foreach (var p in s.PsdLayers)
                if (day.Find(p) == null && night?.Find(p) == null) plan.Missing.Add($"{label}: レイヤー「{p}」が PSD にありません");

            var dayPath = Full(s.Get("DaytimeImage"));
            var nightPath = Full(s.Get("NighttimeImage"));
            switch (s.Kind)
            {
                // メインパネルの表示灯は [This] と同じく、パネル本体のレイヤーを重ねる（位置は動かさない）
                case SectionKind.PilotLamp when s.IsMainPanel:
                case SectionKind.This:
                    {
                        var d = PsdImport.ComposeBase(day, s.PsdLayers, plan.Warnings, "昼");
                        if (d != null && dayPath != null) plan.Files.Add(new(dayPath, PngWriter.EncodePow2(d)));
                        if (night != null && nightPath != null && PsdImport.ComposeBase(night, s.PsdLayers, plan.Warnings, "夜", allSameName: true) is { } n)
                            plan.Files.Add(new(nightPath, PngWriter.EncodePow2(n)));
                        break;
                    }
                case SectionKind.PilotLamp:
                    {
                        var lamp = PsdImport.RenderLamp(day, night, s.PsdLayers[0], plan.Warnings);
                        if (lamp == null) break;
                        if (dayPath != null) plan.Files.Add(new(dayPath, PngWriter.EncodePow2(lamp.Day)));
                        if (lamp.Night != null && nightPath != null) plan.Files.Add(new(nightPath, PngWriter.EncodePow2(lamp.Night)));
                        CheckMove(plan, s, i, lamp.Rect);
                        break;
                    }
                case SectionKind.DigitalNumber:
                    {
                        var num = PsdImport.RenderNumber(day, night, s.PsdLayers, plan.Warnings);
                        if (num == null) break;
                        if (dayPath != null) plan.Files.Add(new(dayPath, PngWriter.EncodePow2(num.Day)));
                        if (num.Night != null && nightPath != null) plan.Files.Add(new(nightPath, PngWriter.EncodePow2(num.Night)));
                        CheckMove(plan, s, i, num.Rect);
                        if ((int)PanelValues.Double(s.Get("Interval"), 0) != num.Rect.Height) plan.Intervals.Add((i, num.Rect.Height));
                        break;
                    }
                default:
                    plan.Warnings.Add($"{label}: {PanelSpec.KindLabel(s.Kind)}は PSD からの更新に対応していません");
                    break;
            }
        }
        plan.Warnings = plan.Warnings.Distinct().ToList();
        return plan;
    }

    private static void CheckMove(PsdUpdatePlan plan, PanelSection s, int index, PsdRect rect)
    {
        if (!PanelValues.TryPoint(s.Get("Location"), out var x, out var y) || (int)x != rect.Left || (int)y != rect.Top)
            plan.Moves.Add((index, rect.Left, rect.Top));
    }

    /// <summary>位置とコマの高さをパネルに反映する（画像の書き出しは呼ぶ側で行う）。</summary>
    public static void ApplyToDocument(PanelDocument doc, PsdUpdatePlan plan, bool moveLocations)
    {
        if (moveLocations)
            foreach (var (i, x, y) in plan.Moves) doc.Sections[i].Set("Location", PanelValues.Point(x, y));
        foreach (var (i, h) in plan.Intervals) doc.Sections[i].Set("Interval", h.ToString());
    }
}
