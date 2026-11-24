namespace BvePanelEditor.Core;

/// <summary>
/// パネルファイルの一部（モニタ・スタフなど、[This] のないパネルファイル）を、今のパネルに 1 つのグループとして足す。
/// モニタくんで書き出したパネルの部分（見出しの行がない）も読める。
/// </summary>
public static class PanelFragment
{
    /// <summary>取り込む準備をしたオブジェクト。</summary>
    public sealed class Prepared
    {
        /// <summary>足すオブジェクト（グループは付け直し済み。画像の場所は元のファイルからのまま）。</summary>
        public List<PanelSection> Sections { get; } = [];
        /// <summary>いちばん外のグループ（取り込んだもの全体）。</summary>
        public string Group { get; set; } = "";
        /// <summary>知らせること（外したメインパネルの印など）。</summary>
        public List<string> Notes { get; } = [];
    }

    /// <summary>
    /// パネルファイルの一部を読む。1 行目が「BveTs Instrument Panel」の見出しでなければ、見出しを補ってから読む
    /// （PanelDocument.Parse は 1 行目を見出しとして読むので、そのままだと 1 行目が消える）。
    /// </summary>
    public static PanelDocument Parse(string text)
    {
        var first = BveText.SplitLines(text).FirstOrDefault() ?? "";
        bool header = first.TrimStart('﻿', ' ', '\t').StartsWith(PanelDocument.HeaderPrefix, StringComparison.OrdinalIgnoreCase);
        return PanelDocument.Parse(header ? text : PanelDocument.DefaultHeader + "\r\n" + text);
    }

    /// <summary>[This] があるか（あれば取り込まない）。</summary>
    public static bool HasThis(PanelDocument fragment) => fragment.Sections.Any(s => s.Kind == SectionKind.This);

    /// <summary>
    /// fragment のオブジェクトを、master に足せる形にする（master は変えない）。
    /// 全体を groupName（重なれば「名前 2」など）のグループに入れ、中のグループはその中の入れ子にする（名前は master と重ならないよう直す）。
    /// パネルくんの付属情報のうち、元のファイルの中でしか意味がないもの（PSD・切り抜き元とのつながり、メインパネルの印）は外す。
    /// </summary>
    public static Prepared Prepare(PanelDocument master, PanelDocument fragment, string groupName)
    {
        var r = new Prepared();
        var used = new HashSet<string>(master.GroupNames());
        r.Group = master.UniqueGroupName(groupName);
        used.Add(r.Group);
        // 中のグループの名前を、master とも取り込むグループとも重ならないように付け直す（同じ名前は同じ名前に）
        var rename = new Dictionary<string, string>();
        foreach (var name in fragment.GroupNames())
        {
            var n = master.UniqueGroupName(name, alsoUsed: used);
            rename[name] = n;
            used.Add(n);
        }
        bool linkDropped = false, mainDropped = false;
        foreach (var s in fragment.Sections)
        {
            var c = s.Clone();
            c.SetGroups([r.Group, .. s.Groups.Select(g => rename[g])]);
            if (c.PsdLayers.Count > 0 || c.CropSet != null) linkDropped = true;
            c.PsdLayers.Clear();
            c.PsdSource = 1;
            c.CropSet = null;
            c.CropShapes.Clear();
            if (c.IsMainPanel) { c.IsMainPanel = false; mainDropped = true; }
            c.ViewHidden = false;
            r.Sections.Add(c);
        }
        if (mainDropped) r.Notes.Add("メインパネルの印は外しました（取り込んだパネルのメインパネルは、ふつうの表示灯になります）");
        if (linkDropped) r.Notes.Add("PSD・切り抜き元とのつながりは外しました");
        return r;
    }

    /// <summary>オブジェクトの画像（DaytimeImage など）のキーと値。</summary>
    public static IEnumerable<(PanelLine Line, string Value)> ImageValues(PanelSection s)
    {
        var keys = PanelSpec.For(s.Kind).Where(k => k.Type == KeyType.Image).Select(k => k.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var l in s.Lines)
        {
            if (l.Kind != PanelLineKind.KeyValue || !keys.Contains(l.Key)) continue;
            var v = l.Value.Trim().Trim('"');
            if (v.Length > 0) yield return (l, v);
        }
    }
}
