using System.Windows;
using System.Windows.Controls;

namespace BvePanelEditor.Themes;

/// <summary>スタイルでは変えられない Fluent の見た目を、読み込まれたときに直す。</summary>
public static class FluentTweaks
{
    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(CheckBox), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnCheckBoxLoaded));
    }

    /// <summary>
    /// チェックボックスの四角（テンプレートで 20px 固定）を 16px にする。色や状態の切り替えは Fluent のまま。
    /// 名前は Fluent のテンプレートの部品名。.NET の更新で名前が変わったら、何もせず 20px のままになる。
    /// </summary>
    static void OnCheckBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.Template == null) return;
        cb.ApplyTemplate();
        if (cb.Template.FindName("ControlBorderIconPresenter", cb) is FrameworkElement box) { box.Width = 16; box.Height = 16; }
        if (cb.Template.FindName("ControlIcon", cb) is TextBlock icon) icon.FontSize = 11;
    }
}
