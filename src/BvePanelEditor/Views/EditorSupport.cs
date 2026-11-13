using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Rendering;

namespace BvePanelEditor.Views;

/// <summary>パネルファイルの色分け。</summary>
public static class PanelHighlighting
{
    private const string Xshd = """
        <SyntaxDefinition name="BvePanel" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Comment" foreground="#3A8A3A" />
          <Color name="Section" foreground="#1B5FC1" fontWeight="bold" />
          <Color name="Key" foreground="#A0303A" />
          <Color name="Header" foreground="#7030A0" fontWeight="bold" />
          <RuleSet ignoreCase="true">
            <Span color="Comment" begin=";|#(?!(?&lt;==\s*#)(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{3})(?![0-9A-Za-z]))" />
            <Rule color="Header">^BveTs[^;#]*</Rule>
            <Rule color="Section">^\s*\[[^\]]*\]</Rule>
            <Rule color="Key">^\s*[A-Za-z0-9_]+(?=\s*=)</Rule>
          </RuleSet>
        </SyntaxDefinition>
        """;

    private static IHighlightingDefinition? _def;

    public static IHighlightingDefinition Definition
    {
        get
        {
            if (_def != null) return _def;
            using var reader = XmlReader.Create(new StringReader(Xshd));
            _def = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            return _def;
        }
    }
}

/// <summary>選択中のセクションの行に薄い背景を付ける。</summary>
public sealed class SectionHighlighter : IBackgroundRenderer
{
    private static readonly Brush Fill = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xC8, 0x00)));
    private static readonly Brush HeaderFill = Freeze(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xC8, 0x00)));
    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    public int FirstLine { get; set; }
    public int HeaderLine { get; set; }
    public int LastLine { get; set; }

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext dc)
    {
        if (FirstLine <= 0) return;
        textView.EnsureVisualLines();
        foreach (var vl in textView.VisualLines)
        {
            int ln = vl.FirstDocumentLine.LineNumber;
            if (ln < FirstLine || ln > LastLine) continue;
            double y = vl.VisualTop - textView.ScrollOffset.Y;
            dc.DrawRectangle(ln == HeaderLine ? HeaderFill : Fill, null, new Rect(0, y, textView.ActualWidth, vl.Height));
        }
    }
}

/// <summary>Windows の色の選択ダイアログ。</summary>
public static class ColorPicker
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CHOOSECOLOR
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public int rgbResult;
        public IntPtr lpCustColors;
        public int Flags;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChooseColorW")]
    private static extern bool ChooseColor(ref CHOOSECOLOR cc);

    private static readonly int[] Custom = new int[16];

    public static Color? Pick(Window owner, Color initial)
    {
        var mem = Marshal.AllocCoTaskMem(16 * 4);
        try
        {
            Marshal.Copy(Custom, 0, mem, 16);
            var cc = new CHOOSECOLOR
            {
                lStructSize = Marshal.SizeOf<CHOOSECOLOR>(),
                hwndOwner = new WindowInteropHelper(owner).Handle,
                rgbResult = initial.R | (initial.G << 8) | (initial.B << 16),
                lpCustColors = mem,
                Flags = 0x1 | 0x2, // CC_RGBINIT | CC_FULLOPEN
            };
            if (!ChooseColor(ref cc)) return null;
            Marshal.Copy(mem, Custom, 0, 16);
            int c = cc.rgbResult;
            return Color.FromRgb((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF));
        }
        finally
        {
            Marshal.FreeCoTaskMem(mem);
        }
    }
}

/// <summary>1 行入力する小さなダイアログ。キャンセルなら null。</summary>
public static class InputDialog
{
    public static string? Show(Window owner, string title, string message, string initial)
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 8) };
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "キャンセル", Width = 80, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(box);
        root.Children.Add(buttons);
        var w = new Window
        {
            Title = title, Content = root, Owner = owner, Width = 380, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
        };
        ok.Click += (_, _) => w.DialogResult = true;
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return w.ShowDialog() == true ? box.Text.Trim() : null;
    }
}

/// <summary>候補から 1 つ選ぶ小さなダイアログ。</summary>
/// <summary>レイヤー（重なりの順）の動かし方。</summary>
/// <summary>レイヤーの操作。Unify は、選んだオブジェクトのレイヤーを主に選んでいるオブジェクトにそろえる。</summary>
public enum LayerOp { Up, Down, Front, Back, Unify }

public static class ChoiceDialog
{
    /// <summary>確認用: 設定すると画面を出さずにこの答えを使う。</summary>
    public static Func<string, IReadOnlyList<string>, int>? TestAnswer;
    /// <summary>確認用: 設定すると、画面を出して画像に保存し、「やめる」（-1）で閉じる。</summary>
    public static string? SnapshotPath;

    public static int Show(Window owner, string title, string message, IReadOnlyList<string> items)
    {
        if (TestAnswer != null) return TestAnswer(message, items);
        // 長い選択肢は折り返す（横に切れないように）
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 3, 2, 3));
        var list = new ListBox
        {
            ItemsSource = items, SelectedIndex = 0, MinHeight = 80, MaxHeight = 320, Margin = new Thickness(0, 8, 0, 8),
            ItemTemplate = new DataTemplate { VisualTree = text },
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "キャンセル", Width = 80, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(list);
        root.Children.Add(buttons);
        var w = new Window
        {
            Title = title, Content = root, Owner = owner, Width = 560, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
        };
        ok.Click += (_, _) => w.DialogResult = true;
        list.MouseDoubleClick += (_, _) => w.DialogResult = true;
        if (SnapshotPath is { } snap)
        {
            w.Loaded += (_, _) => w.Dispatcher.BeginInvoke(() =>
            {
                w.UpdateLayout();
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);
                double W = root.ActualWidth + 24, H = root.ActualHeight + 24;
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(W * dpi.DpiScaleX), (int)(H * dpi.DpiScaleY),
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
                var host = new System.Windows.Media.DrawingVisual();
                using (var dc = host.RenderOpen())
                {
                    dc.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(0, 0, W, H));
                    dc.DrawRectangle(new System.Windows.Media.VisualBrush(root), null, new Rect(12, 12, root.ActualWidth, root.ActualHeight));
                }
                rtb.Render(host);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using (var fs = System.IO.File.Create(snap)) enc.Save(fs);
                w.DialogResult = false;
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        return w.ShowDialog() == true ? list.SelectedIndex : -1;
    }
}
