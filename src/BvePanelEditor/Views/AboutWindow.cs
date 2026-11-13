using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace BvePanelEditor.Views;

/// <summary>ヘルプ → バージョン情報。版・ライセンス・使っているライブラリと同梱素材のクレジット。</summary>
public sealed class AboutWindow : Window
{
    /// <summary>画面に出す版。</summary>
    public const string DisplayVersion = "0.3α";

    public AboutWindow(Window owner)
    {
        Owner = owner;
        Title = $"{MainWindow.AppName} のバージョン情報";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Yu Gothic UI");
        FontSize = 13;

        var root = new StackPanel();
        root.Children.Add(new TextBlock { Text = MainWindow.AppName, FontSize = 24, FontWeight = FontWeights.Bold });
        root.Children.Add(new TextBlock { Text = $"バージョン {DisplayVersion}", FontSize = 14, Margin = new Thickness(0, 2, 0, 0) });
        root.Children.Add(new TextBlock
        {
            Text = "BVE Trainsim 5 / 6 の運転台パネルファイルを、画面で見ながら編集するツールです。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
        });
        root.Children.Add(new TextBlock { Text = "Copyright (c) 2026 ntfiv　MIT License", Margin = new Thickness(0, 10, 0, 0) });
        root.Children.Add(new TextBlock
        {
            Text = $".NET {Environment.Version}", Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 2, 0, 0),
        });

        root.Children.Add(new TextBlock { Text = "使っているライブラリ・同梱している素材", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 4) });
        root.Children.Add(Credit("AvalonEdit（テキスト欄）", "MIT License　Copyright (c) 2000-2023 AlphaSierraPapa for the SharpDevelop Team", "https://github.com/icsharpcode/AvalonEdit"));
        root.Children.Add(Credit("RealAnalogGauge（BVE Workshop 作の ATS プラグイン）",
            "動作プレビューで針の動きを再現する計算は、RealAnalogGauge のソースを参考に移植しています。MIT License　Copyright (c) 2015 BVE Workshop",
            "https://bvews.jpn.org/materials/real-analog-gauge/"));
        root.Children.Add(Credit("標準の針（needles）", "作者の自作素材（CC0）", null));
        root.Children.Add(Credit("E217 の針（needle_E217）", "内房線製作チーム制作の E217 系に同梱のものを使用させていただいています", "https://bvets.net/uchibo/"));
        root.Children.Add(Credit("サンプル（sample）", "作者が過去に公開した車両の素材。動作確認用で、フリー素材ではなく MIT ライセンスの対象外です", null));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var license = new Button { Content = "ライセンスを見る", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0) };
        license.Click += (_, _) => OpenFile("LICENSE.txt");
        var notices = new Button { Content = "ライブラリ・素材の表示を見る", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0) };
        notices.Click += (_, _) => OpenFile("THIRD-PARTY-NOTICES.txt");
        var ok = new Button { Content = "閉じる", Width = 90, IsDefault = true, IsCancel = true };
        ok.Click += (_, _) => Close();
        buttons.Children.Add(license);
        buttons.Children.Add(notices);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        // 余白は Border の Padding で付ける（画面を画像に保存したときに端が切れないように）
        Content = new Border { Padding = new Thickness(20), Child = root };
    }

    private static FrameworkElement Credit(string name, string text, string? url)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 2, 0, 4) };
        tb.Inlines.Add(new Run("・" + name) { FontWeight = FontWeights.SemiBold });
        tb.Inlines.Add(new LineBreak());
        tb.Inlines.Add(new Run("　" + text) { Foreground = Brushes.DimGray, FontSize = 12 });
        if (url != null)
        {
            tb.Inlines.Add(new LineBreak());
            tb.Inlines.Add(new Run("　"));
            var link = new Hyperlink(new Run(url)) { NavigateUri = new Uri(url) };
            link.RequestNavigate += (_, e) =>
            {
                try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            };
            tb.Inlines.Add(link);
        }
        return tb;
    }

    private void OpenFile(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, name);
        if (!File.Exists(path)) { MessageBox.Show(this, $"{name} が見つかりません。", Title, MessageBoxButton.OK, MessageBoxImage.Information); return; }
        try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, $"{name} を開けませんでした:\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
