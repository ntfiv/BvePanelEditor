using System.Windows;
using BvePanelEditor.Core;
using BvePanelEditor.Views;

namespace BvePanelEditor;

/// <summary>レイヤー（Layer、重なりの順）を上げる・下げる。選んでいるオブジェクトをまとめて動かす。</summary>
public partial class MainWindow
{
    /// <summary>レイヤーを持つオブジェクトか（パネル本体などは除く）。</summary>
    private static bool HasLayer(PanelSection s) => s.Kind is SectionKind.PilotLamp or SectionKind.Needle or SectionKind.DigitalNumber or SectionKind.DigitalGauge;

    private static int LayerOf(PanelSection s) => (int)Math.Round(PanelValues.Double(s.Get("Layer"), 0));

    /// <summary>選んでいるオブジェクトのうち、レイヤーを持つもの。</summary>
    private List<int> LayerTargets()
    {
        if (_doc == null) return [];
        var set = _selSet.Count > 0 ? _selSet : _selected >= 0 ? [_selected] : [];
        return set.Where(i => i >= 0 && i < _doc.Sections.Count && HasLayer(_doc.Sections[i])).Distinct().ToList();
    }

    /// <summary>
    /// 選んだオブジェクトのレイヤーを layer にそろえる（null なら主に選んでいるオブジェクトのレイヤー）。
    /// 重なっているオブジェクト（選んでいないオブジェクトとの組も）の手前・奥が入れ替わるとき（指針とふた、赤と黒の針など）は、一覧を出して確認する。
    /// </summary>
    public void UnifyLayers(IReadOnlyList<int> targets, int? layer)
    {
        if (_doc == null || targets.Count < 2) { Message("レイヤーをそろえるオブジェクトを 2 つ以上選んでください"); return; }
        int to = layer ?? LayerOf(_doc.Sections[targets.Contains(_selected) ? _selected : targets[0]]);
        if (targets.All(i => LayerOf(_doc.Sections[i]) == to)) { Message($"レイヤーはもうそろっています（{to}）"); return; }
        var conflicts = LayerOrderChanges(targets.ToDictionary(i => i, _ => to));
        if (conflicts.Count > 0 && !Confirm(
                $"レイヤーを {to} にそろえると、次の重なっているオブジェクトの手前・奥が入れ替わり、BVE での見え方が変わります。\n\n"
                + string.Join("\n", conflicts.Take(8)) + (conflicts.Count > 8 ? $"\n…ほか {conflicts.Count - 8} 組" : "")
                + "\n\n（同じレイヤーのオブジェクトは、パネルファイルで後ろにあるほど手前に描かれます）\n続けますか？"))
            return;
        foreach (var i in targets) _doc.Sections[i].Set("Layer", to.ToString());
        CommitModel();
        _props.Invalidate();
        ShowSelectionDetails(scroll: false);
        Message($"{targets.Count} 個のレイヤーを {to} にそろえました" + (conflicts.Count > 0 ? $"（手前・奥が入れ替わった組 {conflicts.Count}）" : ""));
    }

    /// <summary>
    /// レイヤーを newLayers のとおりに変えたとき、重なっているオブジェクトの組で手前・奥が入れ替わるもの（「A（指針・L2）と B（表示灯・L1）」の形）。
    /// 描く順は、レイヤーの小さい順、同じならファイルの順。
    /// </summary>
    private List<string> LayerOrderChanges(IReadOnlyDictionary<int, int> newLayers)
    {
        var list = new List<string>();
        if (_doc == null || _scene == null) return list;
        var items = _scene.Items.Where(it => !it.IsMainPanel).ToList();
        int Old(int i) => LayerOf(_doc.Sections[i]);
        int New(int i) => newLayers.TryGetValue(i, out var l) ? l : Old(i);
        string Name(int i) => $"{(_doc.Sections[i].Title.Length > 0 ? _doc.Sections[i].Title : PanelSpec.KindLabel(_doc.Sections[i].Kind))}（{PanelSpec.KindLabel(_doc.Sections[i].Kind)}・L{Old(i)}）";
        for (int x = 0; x < items.Count; x++)
            for (int y = x + 1; y < items.Count; y++)
            {
                int a = items[x].Section, b = items[y].Section;
                if (!newLayers.ContainsKey(a) && !newLayers.ContainsKey(b)) continue;
                if (!Bounds(items[x]).IntersectsWith(Bounds(items[y]))) continue;
                bool aFrontOld = (Old(a), a).CompareTo((Old(b), b)) > 0;
                bool aFrontNew = (New(a), a).CompareTo((New(b), b)) > 0;
                if (aFrontOld != aFrontNew) list.Add($"・{Name(aFrontOld ? a : b)} が手前 → {Name(aFrontOld ? b : a)} が手前に");
            }
        return list;
    }

    /// <summary>レイヤーを動かす。選んだオブジェクトどうしの上下の順は変えない。レイヤーは 0 より小さくしない。</summary>
    public void ChangeLayers(LayerOp op)
    {
        if (_doc == null) return;
        FlushReparse();
        var targets = LayerTargets();
        if (targets.Count == 0) { Message("レイヤーを持つオブジェクト（表示灯・指針・デジタル数字・円形ゲージ）を選んでください"); return; }
        if (op == LayerOp.Unify) { UnifyLayers(targets, null); return; }
        var layers = targets.ToDictionary(i => i, i => LayerOf(_doc.Sections[i]));
        int minSel = layers.Values.Min(), maxSel = layers.Values.Max();
        var others = Enumerable.Range(0, _doc.Sections.Count).Where(i => !layers.ContainsKey(i) && HasLayer(_doc.Sections[i])).Select(i => LayerOf(_doc.Sections[i])).ToList();
        int shift = op switch
        {
            LayerOp.Up => 1,
            LayerOp.Down => -1,
            // 最前面: ほかのどれよりも上へ（ほかがなければそのまま）
            LayerOp.Front => others.Count == 0 ? 0 : Math.Max(0, others.Max() + 1 - minSel),
            // 最背面: ほかのどれよりも下へ。0 より下にはできないので、そのときは 0 から
            LayerOp.Back => others.Count == 0 ? 0 : Math.Min(0, Math.Max(others.Min() - 1 - maxSel, -minSel)),
            _ => 0,
        };
        if (minSel + shift < 0) shift = -minSel;
        if (shift == 0)
        {
            Message(op switch
            {
                LayerOp.Down or LayerOp.Back when minSel == 0 => "レイヤーはこれより下げられません（0 がいちばん下）",
                LayerOp.Front => "すでにいちばん上です",
                LayerOp.Back => "すでにいちばん下です（ほかのオブジェクトと同じレイヤー 0 のときは、パネルファイルの書き順で決まります）",
                _ => "レイヤーは変わりませんでした",
            });
            return;
        }
        foreach (var (i, l) in layers) _doc.Sections[i].Set("Layer", (l + shift).ToString());
        CommitModel();
        _props.Invalidate();
        ShowSelectionDetails(scroll: false);
        int nMin = minSel + shift, nMax = maxSel + shift;
        Message($"{targets.Count} 個のレイヤーを{(shift > 0 ? $" {shift} 上げ" : $" {-shift} 下げ")}ました（{(nMin == nMax ? $"{nMin}" : $"{nMin}〜{nMax}")}）");
    }

    /// <summary>
    /// 一覧の下の「そろえる ▾」: そろえる先を選ぶメニュー（最初に選んだオブジェクトのレイヤー・今使っているレイヤー・ほかの数）。
    /// </summary>
    private void OnUnifyLayerButton(object? sender, RoutedEventArgs? e)
    {
        if (_doc == null) return;
        FlushReparse();
        if (BuildUnifyLayerMenu() is not { } menu) { Message("レイヤーをそろえるオブジェクトを 2 つ以上選んでください（一覧で Ctrl／Shift＋クリック）"); return; }
        menu.PlacementTarget = UnifyLayerButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    /// <summary>「そろえる ▾」のメニュー（レイヤーを持つオブジェクトを 2 つ以上選んでいないときは null）。</summary>
    private System.Windows.Controls.ContextMenu? BuildUnifyLayerMenu()
    {
        if (_doc == null) return null;
        var targets = LayerTargets();
        if (targets.Count < 2) return null;
        int first = LayerOf(_doc.Sections[targets.Contains(_selected) ? _selected : targets[0]]);
        var menu = new System.Windows.Controls.ContextMenu();
        void Add(string header, int to)
        {
            var mi = new System.Windows.Controls.MenuItem { Header = header };
            mi.Click += (_, _) => UnifyLayers(LayerTargets(), to);
            menu.Items.Add(mi);
        }
        Add($"{first}（最初に選んだオブジェクトのレイヤー）", first);
        var others = targets.Select(i => LayerOf(_doc.Sections[i])).Where(l => l != first).Distinct().OrderByDescending(l => l).ToList();
        if (others.Count > 0) menu.Items.Add(new System.Windows.Controls.Separator());
        foreach (var l in others) Add($"{l}（{targets.Count(i => LayerOf(_doc.Sections[i]) == l)} 個が使用中）", l);
        menu.Items.Add(new System.Windows.Controls.Separator());
        var any = new System.Windows.Controls.MenuItem { Header = "ほかのレイヤー…" };
        any.Click += (_, _) =>
        {
            var s = InputDialog.Show(this, "レイヤーをそろえる", "そろえる先のレイヤーを入れてください（0 以上の整数）", first.ToString());
            if (s == null) return;
            if (int.TryParse(s.Trim(), out var to) && to >= 0) UnifyLayers(LayerTargets(), to);
            else Message("レイヤーは 0 以上の整数で入れてください");
        };
        menu.Items.Add(any);
        return menu;
    }

    private void OnLayerUp(object? sender, RoutedEventArgs? e) => ChangeLayers(LayerOp.Up);
    private void OnLayerDown(object? sender, RoutedEventArgs? e) => ChangeLayers(LayerOp.Down);
    private void OnLayerFront(object? sender, RoutedEventArgs? e) => ChangeLayers(LayerOp.Front);
    private void OnLayerBack(object? sender, RoutedEventArgs? e) => ChangeLayers(LayerOp.Back);
}
