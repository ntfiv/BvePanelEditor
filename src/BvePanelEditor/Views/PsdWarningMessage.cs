using BvePanelEditor.Core.Psd;

namespace BvePanelEditor.Views;

/// <summary>
/// PSD の注意（△ 近似／× 再現不可）を種類ごとに分けたダイアログ文にする。
/// 読み込み画面とレイヤー振り分け画面で、出し方をそろえるための共通部品。
/// </summary>
internal static class PsdWarningMessage
{
    /// <summary>見出し＋△・× のグループ（各 15 件まで、残りは「…ほか N 件」）。</summary>
    public static string Build(string intro, IEnumerable<PsdWarning> warnings)
    {
        var list = warnings.ToList();
        var parts = new List<string> { intro };
        void Group(PsdWarningKind kind, string head)
        {
            var ws = list.Where(w => w.Kind == kind).Select(w => w.Text).Distinct().ToList();
            if (ws.Count == 0) return;
            parts.Add(head + "\n" + string.Join("\n", ws.Take(15).Select(w => "・" + w))
                + (ws.Count > 15 ? $"\n…ほか {ws.Count - 15} 件" : ""));
        }
        Group(PsdWarningKind.Approx, "△ 近似して再現（Photoshop と見た目が少し変わります）");
        Group(PsdWarningKind.Unsupported, "× 再現不可（その部分は出ません）");
        return string.Join("\n\n", parts);
    }
}
