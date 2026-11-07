using System.Windows;
using BvePanelEditor.Core;
using BvePanelEditor.Views;

namespace BvePanelEditor;

/// <summary>レイヤー（Layer、重なりの順）を上げる・下げる。選んでいる計器をまとめて動かす。</summary>
public partial class MainWindow
{
    /// <summary>レイヤーを持つ計器か（パネル本体などは除く）。</summary>
    private static bool HasLayer(PanelSection s) => s.Kind is SectionKind.PilotLamp or SectionKind.Needle or SectionKind.DigitalNumber or SectionKind.DigitalGauge;

    private static int LayerOf(PanelSection s) => (int)Math.Round(PanelValues.Double(s.Get("Layer"), 0));

    /// <summary>選んでいる計器のうち、レイヤーを持つもの。</summary>
    private List<int> LayerTargets()
    {
        if (_doc == null) return [];
        var set = _selSet.Count > 0 ? _selSet : _selected >= 0 ? [_selected] : [];
        return set.Where(i => i >= 0 && i < _doc.Sections.Count && HasLayer(_doc.Sections[i])).Distinct().ToList();
    }

    /// <summary>レイヤーを動かす。選んだ計器どうしの上下の順は変えない。レイヤーは 0 より小さくしない。</summary>
    public void ChangeLayers(LayerOp op)
    {
        if (_doc == null) return;
        FlushReparse();
        var targets = LayerTargets();
        if (targets.Count == 0) { Message("レイヤーを持つ計器（表示灯・指針・デジタル数字・円形ゲージ）を選んでください"); return; }
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
                LayerOp.Back => "すでにいちばん下です（ほかの計器と同じレイヤー 0 のときは、パネルファイルの書き順で決まります）",
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

    private void OnLayerUp(object? sender, RoutedEventArgs? e) => ChangeLayers(LayerOp.Up);
    private void OnLayerDown(object? sender, RoutedEventArgs? e) => ChangeLayers(LayerOp.Down);
    private void OnLayerFront(object? sender, RoutedEventArgs? e) => ChangeLayers(LayerOp.Front);
    private void OnLayerBack(object? sender, RoutedEventArgs? e) => ChangeLayers(LayerOp.Back);
}
