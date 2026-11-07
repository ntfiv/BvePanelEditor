namespace BvePanelEditor.Core;

public enum BveFileKind { Panel, Vehicle, Scenario, Unknown }

/// <summary>車両ファイル・シナリオファイルからパネルや ATS の場所を探す。</summary>
public static class BveFiles
{
    public static BveFileKind Detect(string text)
    {
        var h = BveText.FirstLine(text).Trim();
        if (h.StartsWith(PanelDocument.HeaderPrefix, StringComparison.OrdinalIgnoreCase)) return BveFileKind.Panel;
        if (h.StartsWith("BveTs Vehicle", StringComparison.OrdinalIgnoreCase)) return BveFileKind.Vehicle;
        if (h.StartsWith("BveTs Scenario", StringComparison.OrdinalIgnoreCase)) return BveFileKind.Scenario;
        // 古い書式のパネル（1 行目が「Version 1.0」で、[This] がある）
        if (PanelDocument.IsLegacyHeaderLine(h) && text.Contains("[This]", StringComparison.OrdinalIgnoreCase)) return BveFileKind.Panel;
        return BveFileKind.Unknown;
    }

    /// <summary>セクションのない "Key = Value" 形式の値を全部読む（キーは小文字）。</summary>
    public static Dictionary<string, string> ReadKeyValues(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = BveText.SplitLines(text);
        for (int i = 1; i < lines.Length; i++)
        {
            var l = PanelLine.Parse(lines[i]);
            if (l.Kind == PanelLineKind.KeyValue) d[l.Key] = l.Value;
        }
        return d;
    }

    public static string Resolve(string baseFile, string relative)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(baseFile)) ?? "";
        return Path.GetFullPath(Path.Combine(dir, relative.Trim().Trim('"')));
    }

    /// <summary>シナリオの Vehicle = a.txt|2; b.txt|1 から車両ファイルの候補を返す。</summary>
    public static List<string> ScenarioVehicles(string scenarioPath, string text)
    {
        var kv = ReadKeyValues(text);
        var list = new List<string>();
        if (!kv.TryGetValue("Vehicle", out var v)) return list;
        foreach (var part in v.Split(';'))
        {
            var p = part.Split('|')[0].Trim();
            if (p.Length == 0) continue;
            list.Add(Resolve(scenarioPath, p));
        }
        return list;
    }
}

/// <summary>車両ファイルの中身（パネルと ATS プラグイン）。</summary>
public sealed record VehicleInfo(string Path, string? PanelPath, string? Ats32Path, string? Ats64Path)
{
    public static VehicleInfo Load(string path, string text)
    {
        var kv = BveFiles.ReadKeyValues(text);
        string? R(string key) => kv.TryGetValue(key, out var v) && v.Trim().Length > 0 ? BveFiles.Resolve(path, v) : null;
        return new VehicleInfo(path, R("Panel"), R("Ats32") ?? R("Ats"), R("Ats64"));
    }
}
