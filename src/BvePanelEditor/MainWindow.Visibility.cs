using System.Windows;
using System.Windows.Input;
using BvePanelEditor.Core;

namespace BvePanelEditor;

/// <summary>
/// 一覧での表示／非表示の切り替え。パネルくんの画面だけのもので、ファイルには書かない（BVE では表示される）。
/// 非表示にした計器はパネル画面に描かず、クリック・囲み・すべて選択でも選ばない（一覧からは選べる）。
/// </summary>
public partial class MainWindow
{
    /// <summary>テキストを読み直したとき、前のモデルで非表示だった計器を新しいモデルでも非表示にする（見出しと名前の並びで対応を取る）。</summary>
    private static void CarryHidden(PanelDocument old, PanelDocument now)
    {
        if (!old.Sections.Any(s => s.ViewHidden)) return;
        if (old.Sections.Count == now.Sections.Count)
        {
            for (int i = 0; i < now.Sections.Count; i++) now.Sections[i].ViewHidden = old.Sections[i].ViewHidden;
            return;
        }
        // 数が変わったとき: 同じ見出し・名前・位置の計器を前から順に対応させる
        static string Key(PanelSection s) => $"{s.HeaderRaw.Trim()}|{s.Title}|{s.Get("Location")}|{s.Get("DaytimeImage")}";
        var hidden = old.Sections.Where(s => s.ViewHidden).Select(Key).GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
        foreach (var s in now.Sections)
            if (hidden.TryGetValue(Key(s), out var n) && n > 0) { s.ViewHidden = true; hidden[Key(s)] = n - 1; }
    }

    /// <summary>非表示を切り替える計器（グループごと選んでいればその中身）。</summary>
    private List<int> HiddenTargets()
    {
        if (_doc == null) return [];
        if (_selGroup != null) return _doc.Members(_selGroup);
        return _selSet.Count > 0 ? [.. _selSet] : _selected >= 0 ? [_selected] : [];
    }

    /// <summary>計器を非表示にする・戻す。全部非表示なら表示に、そうでなければ非表示にする。</summary>
    public void ToggleHidden(IReadOnlyList<int> targets)
    {
        if (_doc == null || targets.Count == 0) return;
        FlushReparse();
        bool hide = !targets.All(i => _doc.Sections[i].ViewHidden);
        foreach (var i in targets) _doc.Sections[i].ViewHidden = hide;
        RebuildAll();
        Message(hide
            ? $"{targets.Count} 個を非表示にしました（パネルくんの画面だけ。ファイルには書かず、BVE では表示されます。一覧の目のマークで戻せます）"
            : $"{targets.Count} 個を表示に戻しました");
    }

    private void OnEyeClick(object sender, MouseButtonEventArgs e)
    {
        if (_doc == null || (sender as FrameworkElement)?.DataContext is not ElementRow row) return;
        // 目のクリックは選択を変えない
        e.Handled = true;
        // バリエーションを表示している間は、そのバリエーションに入れる・外す
        if (_viewVariation != null)
        {
            ToggleMembership(row.IsHeader ? _doc.Members(row.GroupName) : row.Index >= 0 ? [row.Index] : [], _viewVariation);
            return;
        }
        if (row.IsHeader) ToggleHidden(_doc.Members(row.GroupName));
        else if (row.Index >= 0) ToggleHidden([row.Index]);
    }

    private void OnToggleHiddenMenu(object? sender, RoutedEventArgs? e) => ToggleHiddenOrMembership();

    /// <summary>Ctrl+H・右クリックの「非表示」: バリエーションを表示中なら、目のマークと同じく、そのバリエーションに入れる・外す。</summary>
    private void ToggleHiddenOrMembership()
    {
        if (_viewVariation != null) ToggleMembership(HiddenTargets(), _viewVariation);
        else ToggleHidden(HiddenTargets());
    }

    // ---- BVE でも非表示（コメントアウト） ----

    /// <summary>コメントアウトできる計器（[This] は除く）。</summary>
    private List<int> DisableTargets(IReadOnlyList<int> targets) =>
        _doc == null ? [] : targets.Where(i => i >= 0 && i < _doc.Sections.Count && _doc.Sections[i].Kind is not (SectionKind.This or SectionKind.Unknown)).ToList();

    /// <summary>
    /// 計器をコメントアウトして無効にする（BVE でも表示しない）・コメントを外して戻す。全部無効なら戻し、そうでなければ無効にする。
    /// 無効の計器はパネル画面に描かず、一覧では背景を灰色にする。中身はそのまま編集できる。
    /// </summary>
    public void ToggleDisabled(IReadOnlyList<int> targets)
    {
        if (_doc == null) return;
        FlushReparse();
        var list = DisableTargets(targets);
        if (list.Count == 0) { Message("コメントアウトできる計器を選んでください（[This] はできません）"); return; }
        bool disable = !list.All(i => _doc.Sections[i].Disabled);
        foreach (var i in list)
        {
            if (disable) _doc.Sections[i].Disable();
            else _doc.Sections[i].Enable();
        }
        CommitModel();
        _props.Invalidate();
        ShowSelectionDetails(scroll: false);
        Message(disable
            ? $"{list.Count} 個をコメントアウトしました（行の頭に「;」。BVE でも表示されません。Ctrl+Shift+H かもう一度右クリックで戻せます）"
            : $"{list.Count} 個のコメントを外して、BVE でも表示するようにしました");
    }

    private void OnToggleDisabledMenu(object? sender, RoutedEventArgs? e) => ToggleDisabled(HiddenTargets());

    private void OnShowAllMenu(object? sender, RoutedEventArgs? e)
    {
        if (_doc == null) return;
        FlushReparse();
        int n = _doc.Sections.Count(s => s.ViewHidden);
        foreach (var s in _doc.Sections) s.ViewHidden = false;
        RebuildAll();
        Message($"非表示にしていた {n} 個をすべて表示に戻しました");
    }
}
