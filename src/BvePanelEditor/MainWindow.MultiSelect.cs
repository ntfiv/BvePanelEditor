using System.Windows;
using System.Windows.Controls;
using BvePanelEditor.Core;
using BvePanelEditor.Rendering;

namespace BvePanelEditor;

/// <summary>整列の種類。</summary>
public enum AlignKind { Left, CenterX, Right, Top, CenterY, Bottom, DistributeX, DistributeY }

/// <summary>複数選択でできること（すべて選択・整列・まとめて編集）。</summary>
public partial class MainWindow
{
    private static bool IsInstrument(PanelSection s) => s.Kind is not (SectionKind.This or SectionKind.Unknown);

    /// <summary>パネル本体（[This]・メインパネル）以外の計器をすべて選ぶ。</summary>
    private void SelectAllElements(object? sender, RoutedEventArgs? e)
    {
        if (_doc == null) return;
        FlushReparse();
        var all = Enumerable.Range(0, _doc.Sections.Count).Where(i => IsInstrument(_doc.Sections[i]) && !_doc.Sections[i].IsMainPanel && !_doc.Sections[i].ViewHidden).ToList();
        if (all.Count == 0) return;
        ApplySelection(all[0], all, null, SelectionSource.Other);
        Message($"計器 {all.Count} 個をすべて選びました");
    }

    /// <summary>選んだ計器に同じ値を書く（そのキーを持てない種類の計器は飛ばす）。</summary>
    private void OnMultiCommit(string key, string value)
    {
        if (_doc == null || _selSet.Count == 0) return;
        FlushReparse();
        int n = 0;
        foreach (var i in _selSet)
        {
            var s = _doc.Sections[i];
            if (!PanelSpec.For(s.Kind).Any(k => k.Name.Equals(key, StringComparison.OrdinalIgnoreCase))) continue;
            s.Set(key, value.Length == 0 ? null : value);
            n++;
        }
        CommitModel();
        Message(value.Length == 0 ? $"{n} 個の計器から {key} を消しました" : $"{n} 個の計器の {key} を「{value}」にしました");
    }

    private void OnAlignMenu(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && Enum.TryParse<AlignKind>(tag, out var k)) Align(k);
    }

    private static Rect BoundsOf(RenderItem it)
    {
        var o = it.Outline();
        return new Rect(new Point(o.Min(p => p.X), o.Min(p => p.Y)), new Point(o.Max(p => p.X), o.Max(p => p.Y)));
    }

    /// <summary>
    /// 整列で一緒に動かすかたまり。グループを丸ごと選んでいれば（ほかの計器と一緒に選んだとき）そのグループで 1 つ。
    /// グループ 1 つだけを選んでいるときは、中の計器を 1 つずつ揃える。
    /// </summary>
    private List<List<int>> AlignUnits()
    {
        var set = _selSet.Where(i => IsInstrument(_doc!.Sections[i]) && _scene?.ItemFor(i) != null).ToList();
        if (_selGroup != null) return set.Select(i => new List<int> { i }).ToList();
        var used = new HashSet<int>();
        var units = new List<List<int>>();
        foreach (var i in set)
        {
            if (used.Contains(i)) continue;
            var g = _doc!.Sections[i].Group;
            var members = g != null ? _doc.Members(g) : null;
            var unit = members != null && members.All(set.Contains) ? members.Where(set.Contains).ToList() : [i];
            foreach (var u in unit) used.Add(u);
            units.Add(unit);
        }
        return units;
    }

    private void Align(AlignKind kind)
    {
        if (_doc == null || _scene == null) return;
        FlushReparse();
        var units = AlignUnits();
        bool distribute = kind is AlignKind.DistributeX or AlignKind.DistributeY;
        if (units.Count < (distribute ? 3 : 2))
        {
            Message(distribute ? "等間隔に並べるには、計器（かたまり）を 3 つ以上選んでください" : "揃えるには、計器（かたまり）を 2 つ以上選んでください");
            return;
        }
        var bounds = units.Select(u => u.Select(i => BoundsOf(_scene.ItemFor(i)!)).Aggregate(Rect.Union)).ToList();
        var all = bounds.Aggregate(Rect.Union);
        var moves = new double[units.Count, 2];
        switch (kind)
        {
            case AlignKind.Left: for (int k = 0; k < units.Count; k++) moves[k, 0] = all.Left - bounds[k].Left; break;
            case AlignKind.Right: for (int k = 0; k < units.Count; k++) moves[k, 0] = all.Right - bounds[k].Right; break;
            case AlignKind.CenterX: for (int k = 0; k < units.Count; k++) moves[k, 0] = (all.Left + all.Right) / 2 - (bounds[k].Left + bounds[k].Right) / 2; break;
            case AlignKind.Top: for (int k = 0; k < units.Count; k++) moves[k, 1] = all.Top - bounds[k].Top; break;
            case AlignKind.Bottom: for (int k = 0; k < units.Count; k++) moves[k, 1] = all.Bottom - bounds[k].Bottom; break;
            case AlignKind.CenterY: for (int k = 0; k < units.Count; k++) moves[k, 1] = (all.Top + all.Bottom) / 2 - (bounds[k].Top + bounds[k].Bottom) / 2; break;
            case AlignKind.DistributeX:
            case AlignKind.DistributeY:
                {
                    // 両端はそのままで、あいだのすき間を同じにする
                    bool x = kind == AlignKind.DistributeX;
                    var order = Enumerable.Range(0, units.Count).OrderBy(k => x ? bounds[k].Left : bounds[k].Top).ToList();
                    double total = order.Sum(k => x ? bounds[k].Width : bounds[k].Height);
                    double gap = ((x ? all.Width : all.Height) - total) / (units.Count - 1);
                    double pos = x ? all.Left : all.Top;
                    foreach (var k in order)
                    {
                        moves[k, x ? 0 : 1] = pos - (x ? bounds[k].Left : bounds[k].Top);
                        pos += (x ? bounds[k].Width : bounds[k].Height) + gap;
                    }
                    break;
                }
        }
        int moved = 0;
        for (int k = 0; k < units.Count; k++)
        {
            double dx = Math.Round(moves[k, 0]), dy = Math.Round(moves[k, 1]);
            if (dx == 0 && dy == 0) continue;
            _doc.Offset(units[k], dx, dy);
            moved++;
        }
        if (moved == 0) { Message("すでに揃っています"); return; }
        CommitModel();
        var label = kind switch
        {
            AlignKind.Left => "左揃え", AlignKind.CenterX => "左右中央揃え", AlignKind.Right => "右揃え",
            AlignKind.Top => "上揃え", AlignKind.CenterY => "上下中央揃え", AlignKind.Bottom => "下揃え",
            AlignKind.DistributeX => "横に等間隔", _ => "縦に等間隔",
        };
        Message($"{label}にしました（{moved} 個動かしました。Ctrl+Z で元に戻せます）");
    }
}
