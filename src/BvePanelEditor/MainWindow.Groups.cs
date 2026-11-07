using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BvePanelEditor.Core;
using BvePanelEditor.Rendering;

namespace BvePanelEditor;

/// <summary>選択がどこから来たか（自分自身へは返さないため）。</summary>
public enum SelectionSource { Other, Canvas, List, Text }

/// <summary>複数選択とグループ。</summary>
public partial class MainWindow
{
    /// <summary>確認の代わりに答えを返す（通し確認用）。null ならメッセージボックスを出す。</summary>
    private Func<string, bool>? _autoConfirm;

    private bool Confirm(string message) =>
        _autoConfirm?.Invoke(message)
        ?? MessageBox.Show(this, message, "確認", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    // ================= 選択 =================

    /// <summary>番号のずれ・消えたグループを直す。グループごとの選択は名前から計器を取り直す。</summary>
    private void NormalizeSelection()
    {
        if (_doc == null) { _selected = -1; _selSet = []; _selGroup = null; return; }
        int n = _doc.Sections.Count;
        if (_selGroup != null)
        {
            var m = _doc.Members(_selGroup);
            if (m.Count == 0) _selGroup = null;
            else
            {
                _selSet = m;
                if (!m.Contains(_selected)) _selected = m[0];
                return;
            }
        }
        _selSet = _selSet.Where(i => i >= 0 && i < n).Distinct().ToList();
        if (_selected >= n) _selected = -1;
        if (_selected >= 0 && !_selSet.Contains(_selected)) _selSet.Insert(0, _selected);
        if (_selected < 0 && _selSet.Count > 0) _selected = _selSet[0];
    }

    /// <summary>選択を決めて、キャンバス・一覧・プロパティ・テキストに伝える。</summary>
    private void ApplySelection(int primary, IReadOnlyList<int> set, string? group, SelectionSource source)
    {
        if (_doc == null) return;
        FlushReparse();
        _selected = primary;
        _selSet = set.ToList();
        _selGroup = group;
        NormalizeSelection();

        if (source != SelectionSource.Canvas)
        {
            // 中に入ったグループの計器を 1 つ選んだときは、中に入ったままにする
            var entered = _selGroup == null && _selSet.Count == 1 && _selected >= 0 && _doc.Sections[_selected].Group is { } g && g == Canvas.EnteredGroup ? g : null;
            Canvas.SetSelection(_selected, _selSet, _selGroup, entered);
        }
        // 折りたたんだグループの中の計器を 1 つずつ選んだときは、そのグループを開いて一覧に出す
        var unfold = _selGroup == null ? _selSet.Select(i => _doc.Sections[i].Group).OfType<string>().Where(_folded.Contains).Distinct().ToList() : [];
        if (unfold.Count > 0)
        {
            foreach (var g in unfold) _folded.Remove(g);
            RebuildAll();
        }
        ShowSelectionDetails(scroll: true);
        UpdateCoverButton();

        if (source != SelectionSource.Text && _selected >= 0)
        {
            var map = _doc.LineMap();
            int line = _selGroup != null && _doc.RunOf(_selGroup) is { } run ? map[run.First].HeaderLine : map[_selected].HeaderLine;
            _suppressCaret = true;
            Editor.ScrollTo(line, 0);
            Editor.TextArea.Caret.Line = line;
            Editor.TextArea.Caret.Column = 1;
            _suppressCaret = false;
        }
        if (source != SelectionSource.Canvas && _selected >= 0 && _scene?.ItemFor(_selected) is { } it)
        {
            var sp = Canvas.ToScreen(it.Location);
            if (sp.X < 0 || sp.Y < 0 || sp.X > Canvas.ActualWidth || sp.Y > Canvas.ActualHeight) Canvas.CenterOn(it.Location);
        }
    }

    /// <summary>プロパティ欄・一覧の選択・テキストの色付け・動作プレビュー欄の色付けを今の選択に合わせる。</summary>
    private void ShowSelectionDetails(bool scroll)
    {
        if (_doc == null) return;
        if (_selGroup != null)
            _props.ShowGroup(_selGroup, _selSet.Count, TopLeftOf(_selSet), _selSet.Select(i => _doc.Sections[i]).ToList());
        else if (_selSet.Count > 1)
            _props.ShowMulti(_selSet.Count, _selSet.Count(i => _doc.Sections[i].Kind is not (SectionKind.This or SectionKind.Unknown)),
                _selSet.Where(i => _doc.Sections[i].Kind is not (SectionKind.This or SectionKind.Unknown)).Select(i => _doc.Sections[i]).ToList());
        else
        {
            // [This] の欄の「メインパネル」の候補（常表示の表示灯）
            _props.MainPanelChoices = Enumerable.Range(0, _doc.Sections.Count)
                .Where(i => _doc.Sections[i].Kind == SectionKind.PilotLamp && string.Equals(_doc.Sections[i].Get("Subject")?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                .Select(i => (i, $"#{i + 1} {(_doc.Sections[i].Title.Length > 0 ? _doc.Sections[i].Title : "表示灯")}（{System.IO.Path.GetFileName(_doc.Sections[i].Get("DaytimeImage") ?? "")}）")).ToList();
            _props.MainPanelIndex = _doc.MainPanel is { } mp ? _doc.Sections.IndexOf(mp) : -1;
            _props.ThisIsBlank = IsBlankThis();
            _props.Show(_selected >= 0 ? _doc.Sections[_selected] : null, _selected);
        }
        SyncListSelection(scroll);
        UpdateHighlight();
        UpdateStateHighlight();
    }

    /// <summary>計器の Location のいちばん左上（位置のない計器は飛ばす）。</summary>
    private Point? TopLeftOf(IEnumerable<int> indices)
    {
        double x = double.MaxValue, y = double.MaxValue;
        bool any = false;
        foreach (var i in indices)
        {
            if (_doc == null || !PanelValues.TryPoint(_doc.Sections[i].Get("Location"), out var lx, out var ly)) continue;
            x = Math.Min(x, lx);
            y = Math.Min(y, ly);
            any = true;
        }
        return any ? new Point(x, y) : null;
    }

    private List<ElementRow> Rows => ElementList.ItemsSource as List<ElementRow> ?? [];

    /// <summary>今の選択を表す一覧の行（グループならその見出し、折りたたまれていればそのグループの見出し）。</summary>
    private ElementRow? RowForSelection()
    {
        if (_doc == null) return null;
        if (_selGroup != null) return Rows.FirstOrDefault(r => r.IsHeader && r.GroupName == _selGroup);
        return Rows.FirstOrDefault(r => !r.IsHeader && r.Index == _selected)
            ?? (_selected >= 0 && _doc.Sections[_selected].Group is { } g ? Rows.FirstOrDefault(r => r.IsHeader && r.GroupName == g) : null);
    }

    private void SyncListSelection(bool scroll)
    {
        _suppressList = true;
        ElementList.SelectedItems.Clear();
        foreach (var r in Rows)
            if (r.IsHeader ? r.GroupName == _selGroup : _selSet.Contains(r.Index)) ElementList.SelectedItems.Add(r);
        if (scroll && RowForSelection() is { } row) ElementList.ScrollIntoView(row);
        _suppressList = false;
    }

    private void OnListSelection(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressList || _doc == null) return;
        var rows = ElementList.SelectedItems.Cast<ElementRow>().ToList();
        if (rows.Count == 0) return;
        var headers = rows.Where(r => r.IsHeader).Select(r => r.GroupName).Distinct().ToList();
        var set = rows.Where(r => !r.IsHeader).Select(r => r.Index).ToList();
        foreach (var h in headers) set.AddRange(_doc.Members(h));
        set = set.Distinct().Order().ToList();
        string? group = headers.Count == 1 && set.All(i => _doc.Sections[i].Group == headers[0]) ? headers[0] : null;

        int primary = -1;
        if (e.AddedItems.OfType<ElementRow>().LastOrDefault() is { } added)
            primary = added.IsHeader ? _doc.Members(added.GroupName).FirstOrDefault(-1) : added.Index;
        if (!set.Contains(primary)) primary = set.FirstOrDefault(-1);
        ApplySelection(primary, set, group, SelectionSource.List);
    }

    private void OnGroupFold(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ElementRow { IsHeader: true } row) return;
        if (!_folded.Remove(row.GroupName)) _folded.Add(row.GroupName);
        RebuildAll();
        e.Handled = true;
    }

    // ================= グループの操作 =================

    private static Rect Bounds(RenderItem it)
    {
        var o = it.Outline();
        return new Rect(new Point(o.Min(p => p.X), o.Min(p => p.Y)), new Point(o.Max(p => p.X), o.Max(p => p.Y)));
    }

    private void OnMakeGroup(object? sender, RoutedEventArgs? e)
    {
        if (_doc == null) return;
        FlushReparse();
        var cands = _selSet.Where(i => _doc.Sections[i].Kind is not (SectionKind.This or SectionKind.Unknown)).Distinct().Order().ToList();
        if (cands.Count == 0)
        {
            Message("グループにする計器を選んでください（一覧で Ctrl／Shift＋クリック、パネル画面で Shift＋クリック）");
            return;
        }
        if (_selGroup != null && cands.All(i => _doc.Sections[i].Group == _selGroup))
        {
            Message($"すでにグループ「{_selGroup}」です");
            return;
        }

        // グループにするとファイル内の並びが変わり、同じレイヤーで重なっている計器の手前・奥が入れ替わることがある
        var reordered = _doc.SectionsReorderedBy(cands);
        var conflicts = new List<string>();
        foreach (var r in reordered)
        {
            if (_scene?.ItemFor(r) is not { } ri) continue;
            foreach (var m in cands)
            {
                if (_scene.ItemFor(m) is not { } mi || mi.Layer != ri.Layer || !Bounds(mi).IntersectsWith(Bounds(ri))) continue;
                conflicts.Add($"・{DisplayName(_doc.Sections[r])} と {DisplayName(_doc.Sections[m])}（Layer {ri.Layer}）");
                break;
            }
        }
        if (conflicts.Count > 0 && !Confirm(
                "グループにするため、ファイル内の並び順を変えます。\n次の計器は同じレイヤーで重なっているので、BVE での手前・奥が入れ替わります。\n\n"
                + string.Join("\n", conflicts.Take(8)) + (conflicts.Count > 8 ? $"\n…ほか {conflicts.Count - 8} 件" : "")
                + "\n\n続けますか？"))
            return;

        var fromOther = cands.Select(i => _doc.Sections[i].Group).Where(g => g != null).Distinct().ToList();
        var name = _doc.NextGroupName();
        var idx = _doc.MakeGroup(cands, name);
        CommitModel();
        ApplySelection(idx[0], idx, name, SelectionSource.Other);
        Message($"「{name}」にしました（{idx.Count} 個）"
            + (reordered.Count > 0 ? $"。間にあった計器 {reordered.Count} 個をグループの後ろへ並べ替えました" : "")
            + (fromOther.Count > 0 ? $"。グループ「{string.Join("」「", fromOther)}」から外れた計器があります" : "")
            + "。名前を入力してください");
        Dispatcher.BeginInvoke(BeginRename, DispatcherPriority.Loaded);
    }

    /// <summary>今の選択のグループ（グループごと選んでいなければ、主に選んでいる計器のグループ）。</summary>
    private string? CurrentGroup() =>
        _selGroup ?? (_doc != null && _selected >= 0 ? _doc.Sections[_selected].Group : null);

    private void OnUngroup(object? sender, RoutedEventArgs? e)
    {
        if (_doc == null) return;
        FlushReparse();
        if (CurrentGroup() is not { } g) { Message("グループを選んでください"); return; }
        var members = _doc.Members(g);
        _doc.Ungroup(g);
        _folded.Remove(g);
        CommitModel();
        ApplySelection(members[0], members, null, SelectionSource.Other);
        Message($"グループ「{g}」を解除しました（計器はそのまま残っています）");
    }

    private void OnRemoveFromGroup(object? sender, RoutedEventArgs? e)
    {
        if (_doc == null || _selected < 0) return;
        FlushReparse();
        if (_selGroup != null || _selSet.Count != 1 || _doc.Sections[_selected].Group is not { } g)
        {
            Message("グループの中の計器を 1 つ選んでください（パネル画面ではダブルクリック、一覧ではその行をクリック）");
            return;
        }
        int ni = _doc.RemoveFromGroup(_selected);
        CommitModel();
        SelectSection(ni);
        Message($"グループ「{g}」から外しました" + (ni != _selected ? "（グループの後ろへ並べ替えました）" : ""));
    }

    private void OnRenameGroupMenu(object? sender, RoutedEventArgs? e)
    {
        if (_doc == null) return;
        if (CurrentGroup() is not { } g) { Message("グループを選んでください"); return; }
        var m = _doc.Members(g);
        if (_selGroup != g) ApplySelection(m[0], m, g, SelectionSource.Other);
        BeginRename();
    }

    private void RenameGroup(string oldName, string newName)
    {
        if (_doc == null) return;
        FlushReparse();
        newName = newName.Trim();
        if (newName.Length == 0 || newName == oldName || _doc.RunOf(oldName) == null) return;
        var n = _doc.RenameGroup(oldName, newName);
        if (_folded.Remove(oldName)) _folded.Add(n);
        CommitModel();
        var m = _doc.Members(n);
        ApplySelection(m[0], m, n, SelectionSource.Other);
        Message(n == newName ? $"グループ名を「{n}」にしました" : $"「{newName}」はほかのグループにあるので「{n}」にしました");
    }

    private void DuplicateGroup(string g)
    {
        if (_doc == null) return;
        var copies = _doc.DuplicateGroup(g, $"{g} のコピー", 10, 10);
        if (copies.Count == 0) return;
        var n = _doc.Sections[copies[0]].Group!;
        CommitModel();
        ApplySelection(copies[0], copies, n, SelectionSource.Other);
        Message($"グループ「{g}」を「{n}」として複製しました（右下に 10px ずらしています）");
    }

    private void DeleteSelected()
    {
        if (_doc == null) return;
        var targets = _selSet.Distinct().OrderDescending().ToList();
        var what = _selGroup != null ? $"グループ「{_selGroup}」の計器 {targets.Count} 個" : $"選んでいる計器 {targets.Count} 個";
        if (targets.Any(i => _doc.Sections[i].Kind == SectionKind.This)) what += "（パネル本体 [This] を含む）";
        if (!Confirm($"{what}を削除しますか？\n（Ctrl+Z で元に戻せます）")) return;
        foreach (var i in targets) _doc.Sections.RemoveAt(i);
        int next = Math.Min(targets[^1], _doc.Sections.Count - 1);
        _selGroup = null;
        CommitModel();
        SelectSection(next);
        Message($"{what}を削除しました（Ctrl+Z で元に戻せます）");
    }

    /// <summary>プロパティ欄でグループの位置（いちばん左上）を書き換えた。</summary>
    private void OnGroupLocationCommit(double x, double y)
    {
        if (_doc == null || _selGroup == null) return;
        FlushReparse();
        if (TopLeftOf(_selSet) is not { } tl) return;
        double dx = x - tl.X, dy = y - tl.Y;
        if (dx == 0 && dy == 0) return;
        _doc.Offset(_selSet, dx, dy);
        CommitModel();
        Message($"グループ「{_selGroup}」を {PanelValues.Num(dx)}, {PanelValues.Num(dy)} 動かしました");
    }
}
