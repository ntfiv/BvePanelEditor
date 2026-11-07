using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BvePanelEditor.Core;

namespace BvePanelEditor;

/// <summary>左の一覧の右クリックメニュー。</summary>
public partial class MainWindow
{
    /// <summary>
    /// 右クリックした行がまだ選ばれていなければ、その行を選ぶ（選んでいる行の上なら、複数選択のまま）。
    /// メニューは押した行（たち）に対して働く。
    /// </summary>
    private void OnListRightDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject d || ItemsControl.ContainerFromElement(ElementList, d) is not ListBoxItem item) return;
        if (item.IsSelected) return;
        ElementList.SelectedItems.Clear();
        item.IsSelected = true;
        item.Focus();
    }

    /// <summary>今の選択で使えるものだけ押せるようにする。</summary>
    private void OnListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_doc == null) { e.Handled = true; return; }
        UpdateListMenu();
    }

    private void UpdateListMenu()
    {
        if (_doc == null) return;
        FlushReparse();
        var sel = _selSet.Count > 0 ? _selSet : _selected >= 0 ? [_selected] : [];
        bool any = sel.Count > 0;
        bool single = sel.Count == 1 && _selGroup == null;
        bool groupable = sel.Any(i => _doc.Sections[i].Kind is not (SectionKind.This or SectionKind.Unknown));
        var group = CurrentGroup();
        bool inGroup = group != null;

        ListMenuRename.IsEnabled = _selected >= 0 && (single || _selGroup != null);
        ListMenuDuplicate.IsEnabled = _selected >= 0 && (single || _selGroup != null);
        ListMenuDelete.IsEnabled = any;
        var targets = HiddenTargets();
        ListMenuToggleHidden.IsEnabled = targets.Count > 0;
        ListMenuToggleHidden.Header = targets.Count > 0 && targets.All(i => _doc.Sections[i].ViewHidden) ? "表示する" : "非表示にする";
        ListMenuShowAll.IsEnabled = _doc.Sections.Any(s => s.ViewHidden);
        var disTargets = DisableTargets(targets);
        ListMenuToggleDisabled.IsEnabled = disTargets.Count > 0;
        ListMenuToggleDisabled.Header = disTargets.Count > 0 && disTargets.All(i => _doc.Sections[i].Disabled)
            ? "BVE で表示する（コメントを外す）" : "BVE でも非表示にする（コメントアウト）";
        ListMenuCover.Visibility = CoverTargets().Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var one = single ? _doc.Sections[_selected] : null;
        ListMenuMakeMain.Visibility = one is { Kind: SectionKind.PilotLamp, IsMainPanel: false } ? Visibility.Visible : Visibility.Collapsed;
        ListMenuThisToMain.Visibility = one is { Kind: SectionKind.This } && _doc.MainPanel == null && !IsBlankThis() && !string.IsNullOrWhiteSpace(one.Get("DaytimeImage"))
            ? Visibility.Visible : Visibility.Collapsed;
        ListMenuUnsetMain.Visibility = one is { IsMainPanel: true } ? Visibility.Visible : Visibility.Collapsed;
        bool layers = LayerTargets().Count > 0;
        ListMenuLayerUp.IsEnabled = ListMenuLayerDown.IsEnabled = ListMenuLayerFront.IsEnabled = ListMenuLayerBack.IsEnabled = layers;
        ListMenuGroup.IsEnabled = groupable;
        ListMenuUngroup.IsEnabled = inGroup;
        ListMenuUngroup.Header = inGroup ? $"グループ「{group}」を解除" : "グループを解除";
        ListMenuRemoveFromGroup.IsEnabled = _selGroup == null && single && _doc.Sections[_selected].Group != null;
        ListMenuRenameGroup.IsEnabled = inGroup;
        ListMenuGroup.Header = groupable && sel.Count > 1 ? $"グループ化する（{sel.Count} 個）" : "グループ化する";
    }
}
