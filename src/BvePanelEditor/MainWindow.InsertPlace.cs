using BvePanelEditor.Core;

namespace BvePanelEditor;

/// <summary>
/// 新しく足すオブジェクトの入れ場所。選んでいるオブジェクト（グループごと選んでいれば、そのグループの最後のオブジェクト）のすぐ下（ファイルで 1 つ後ろ。同じレイヤーなら BVE ではすぐ手前に描かれる）に入れ、
/// レイヤーとグループはそのオブジェクトに合わせる。何も選んでいない（[This] だけ）なら、今までどおりファイルの最後に入れる。
/// </summary>
public partial class MainWindow
{
    /// <summary>入れる位置・レイヤー（null なら各所の決め方のまま）・入るグループ。</summary>
    private readonly record struct InsertPlace(int At, int? Layer, List<string> Groups);

    private InsertPlace GetInsertPlace()
    {
        if (_doc == null) return new(0, null, []);
        static int LayerOf(PanelSection s) => (int)Math.Round(PanelValues.Double(s.Get("Layer"), 0));
        if (_selGroup != null && _doc.RunOf(_selGroup) is { } run)
            return new(run.Last + 1, LayerOf(_doc.Sections[run.Last]), _doc.ParentPath(_selGroup)); // グループの下（グループの外）
        if (_selected >= 0 && _selected < _doc.Sections.Count && _doc.Sections[_selected].Kind is not (SectionKind.This or SectionKind.Unknown))
            return new(_selected + 1, LayerOf(_doc.Sections[_selected]), [.. _doc.Sections[_selected].Groups]);
        return new(_doc.Sections.Count, null, []);
    }

    /// <summary>
    /// オブジェクトを入れる（k 番目に足すもの。place.At + k に入れる）。レイヤーは place のもの＋layerOffset（place にレイヤーがなければ sec のまま）。
    /// グループは place のもの。バリエーションを表示中なら、そのバリエーションだけに入れる。戻り値は入れた番号。
    /// </summary>
    private int InsertNewSection(PanelSection sec, InsertPlace place, int k, int layerOffset = 0)
    {
        if (place.Layer is { } layer) sec.Set("Layer", (layer + layerOffset).ToString());
        sec.SetGroups(place.Groups);
        JoinViewVariation(sec);
        int at = Math.Min(place.At + k, _doc!.Sections.Count);
        _doc.Sections.Insert(at, sec);
        return at;
    }
}
