using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BvePanelEditor.Core;
using BvePanelEditor.Core.Psd;
using BvePanelEditor.Rendering;

namespace BvePanelEditor.Views;

/// <summary>
/// デジタル数字（DigitalNumber）のテスト画面。1 コマの高さ（Interval）を確かめ、おかしければ手で直す。
/// 左に全体のコマ割（区切り線と今のコマの枠）、右に BVE での見え方（今の値のコマだけ）を出す。
/// </summary>
public sealed class DigitTestWindow : Window
{
    /// <summary>確認用: 設定すると画面を出さずに、作った画面をこの関数に渡す（true を返せば「置く」）。</summary>
    public static Func<DigitTestWindow, bool>? TestAnswer;

    private readonly PsdPixels _day;
    private PsdPixels? _night;
    private readonly int _contentHeight;
    private readonly BitmapSource _dayBmp;
    private BitmapSource? _nightBmp;
    private readonly bool _allowNightPick;
    private readonly TextBlock _nightText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Foreground = Brushes.DimGray };
    private readonly StackPanel _dnSwitch = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly StripView _strip = new();
    private readonly FrameView _frame = new();
    private readonly TextBox _interval = new() { Width = 60, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Slider _value = new() { Minimum = 0, IsSnapToTickEnabled = true, TickFrequency = 1, Width = 260 };
    private readonly TextBlock _valueText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), MinWidth = 90 };
    private readonly CheckBox _auto = new() { Content = "自動で送る", Margin = new Thickness(0, 6, 0, 0) };
    private readonly RadioButton _showDay = new() { Content = "昼", IsChecked = true, GroupName = "dn", Margin = new Thickness(0, 0, 10, 0) };
    private readonly RadioButton _showNight = new() { Content = "夜", GroupName = "dn" };
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBox _subject = new() { Width = 160, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private string _detectNote;

    /// <summary>夜の画像（「夜の画像を選ぶ…」で変えたもの）。</summary>
    public PsdPixels? Night => _night;
    /// <summary>「夜の画像を選ぶ…」で選んだファイル（選んでいなければ null）。</summary>
    public string? NightPath { get; private set; }
    /// <summary>夜の画像を選び直した・外した。</summary>
    public bool NightChanged { get; private set; }

    /// <summary>決まった 1 コマの高さ。</summary>
    public int Interval { get; private set; }
    /// <summary>状態量（Subject）。</summary>
    public string Subject => _subject.Text.Trim();
    public int FrameCount => DigitStrip.Count(_contentHeight, Interval).Frames;

    /// <param name="contentHeight">中身の高さ（2 の累乗に広げた画像なら広げる前の高さ）。</param>
    /// <param name="detectNote">Interval をどう決めたか（画面に出す）。</param>
    /// <param name="allowNightPick">「夜の画像を選ぶ…」「外す」を出す（コマ割済の画像のとき）。</param>
    public DigitTestWindow(Window owner, string name, PsdPixels day, PsdPixels? night, int contentHeight, int interval, string detectNote, string subject,
        bool allowNightPick = false, string? nightName = null)
    {
        _allowNightPick = allowNightPick;
        _nightText.Text = night != null ? $"夜: {nightName ?? "あり"}" : "夜の画像: なし";
        Owner = owner;
        Title = $"デジタル数字のテスト - {name}";
        Width = 820; Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Yu Gothic UI");
        FontSize = 12;
        Background = SystemColors.WindowBrush;
        _day = day;
        _night = night;
        _contentHeight = Math.Clamp(contentHeight, 1, day.Height);
        _dayBmp = ImageLoader.ToBitmap(day);
        _nightBmp = night != null ? ImageLoader.ToBitmap(night) : null;
        _detectNote = detectNote;
        _subject.Text = subject;
        Content = BuildUi();
        SetInterval(interval);
        _timer.Tick += (_, _) => _value.Value = _value.Value >= _value.Maximum ? 0 : _value.Value + 1;
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>画面を出す（確認用の答えがあれば出さない）。「置く」なら true。</summary>
    public bool Ask() => TestAnswer != null ? TestAnswer(this) : ShowDialog() == true;

    private UIElement BuildUi()
    {
        var root = new DockPanel();
        var top = new TextBlock
        {
            Text = "BVE のデジタル数字は、画像を上から 1 コマの高さ（Interval）ずつ区切り、状態量の値の番号のコマを出します。"
                + "左の赤い線がコマの区切りです。数字がずれて見えるときは、Interval を直してください。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        };
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Button { Content = "この設定で置く", Padding = new Thickness(14, 4, 14, 4), IsDefault = true, Margin = new Thickness(0, 0, 8, 0), FontWeight = FontWeights.SemiBold };
        ok.Click += (_, _) => { if (Interval > 0) DialogResult = true; };
        var cancel = new Button { Content = "キャンセル", Width = 90, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });

        var left = new GroupBox { Header = "コマ割（画像全体）", Margin = new Thickness(0, 0, 8, 0) };
        left.Content = new ScrollViewer { Content = _strip, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.DimGray };
        grid.Children.Add(left);

        var right = new StackPanel();
        Grid.SetColumn(right, 1);
        var look = new GroupBox { Header = "BVE での見え方（今の値のコマ）" };
        look.Content = new Border { Child = _frame, Height = 230, Background = Brushes.DimGray, Margin = new Thickness(4) };
        right.Children.Add(look);

        var valueRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        valueRow.Children.Add(new TextBlock { Text = "値", VerticalAlignment = VerticalAlignment.Center, Width = 70 });
        valueRow.Children.Add(_value);
        valueRow.Children.Add(_valueText);
        right.Children.Add(valueRow);
        _value.ValueChanged += (_, _) => Redraw();
        var autoRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(70, 0, 0, 0) };
        autoRow.Children.Add(_auto);
        _auto.Checked += (_, _) => _timer.Start();
        _auto.Unchecked += (_, _) => _timer.Stop();
        right.Children.Add(autoRow);
        // 昼・夜の切り替えと、夜の画像（コマ割済の画像なら選べる）
        var nightRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        nightRow.Children.Add(new TextBlock { Text = "表示", VerticalAlignment = VerticalAlignment.Center, Width = 70 });
        _dnSwitch.Children.Add(_showDay);
        _dnSwitch.Children.Add(_showNight);
        _showDay.Checked += (_, _) => Redraw();
        _showNight.Checked += (_, _) => Redraw();
        nightRow.Children.Add(_dnSwitch);
        nightRow.Children.Add(_nightText);
        if (_allowNightPick)
        {
            var pick = new Button { Content = "夜の画像を選ぶ…", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(10, 0, 4, 0) };
            pick.Click += (_, _) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "夜の画像", Filter = "画像|*.png;*.bmp;*.jpg;*.jpeg;*.gif;*.tif;*.tiff|すべてのファイル|*.*" };
                if (dlg.ShowDialog(this) == true) SetNightFile(dlg.FileName);
            };
            var clear = new Button { Content = "外す", Padding = new Thickness(8, 1, 8, 1) };
            clear.Click += (_, _) => SetNightFile(null);
            nightRow.Children.Add(pick);
            nightRow.Children.Add(clear);
        }
        right.Children.Add(nightRow);

        var intRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        intRow.Children.Add(new TextBlock { Text = "1 コマの高さ", VerticalAlignment = VerticalAlignment.Center, Width = 70, ToolTip = "Interval（ピクセル）" });
        intRow.Children.Add(_interval);
        _interval.LostFocus += (_, _) => CommitInterval();
        _interval.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) CommitInterval(); };
        var up = new RepeatButton { Content = "▲", Width = 24, Margin = new Thickness(2, 0, 0, 0) };
        up.Click += (_, _) => SetInterval(Interval + 1);
        var down = new RepeatButton { Content = "▼", Width = 24 };
        down.Click += (_, _) => SetInterval(Interval - 1);
        intRow.Children.Add(up);
        intRow.Children.Add(down);
        intRow.Children.Add(new TextBlock { Text = "px", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 0) });
        var redetect = new Button { Content = "画像から求め直す", Padding = new Thickness(8, 2, 8, 2) };
        redetect.Click += (_, _) => Redetect();
        intRow.Children.Add(redetect);
        right.Children.Add(intRow);
        right.Children.Add(_info);

        var subRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        subRow.Children.Add(new TextBlock { Text = "状態量", VerticalAlignment = VerticalAlignment.Center, Width = 70, ToolTip = "Subject。値の番号のコマを出します" });
        subRow.Children.Add(_subject);
        subRow.Children.Add(new TextBlock
        {
            Text = "（あとでパネル画面でも直せます）", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
        });
        right.Children.Add(subRow);
        grid.Children.Add(right);
        root.Children.Add(grid);
        // 余白は Border の Padding で付ける（画面を画像に保存したときに端が切れないように）
        return new Border { Padding = new Thickness(10), Child = root };
    }

    /// <summary>夜の画像を選ぶ（null なら外す）。昼と大きさが違えば知らせる（どちらも同じ加工をする）。</summary>
    public bool SetNightFile(string? path)
    {
        PsdPixels? px = null;
        if (path != null)
        {
            px = ImageLoader.TryLoadPixels(path);
            if (px == null)
            {
                if (TestAnswer == null) MessageBox.Show(this, "画像を読めませんでした。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if ((px.Width, px.Height) != (_day.Width, _day.Height) && TestAnswer == null)
                MessageBox.Show(this, $"夜の画像の大きさ {px.Width}×{px.Height} が、昼の画像（{_day.Width}×{_day.Height}）と違います。コマの位置がずれていないか確かめてください。",
                    Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        _night = px;
        _nightBmp = px != null ? ImageLoader.ToBitmap(px) : null;
        NightPath = path;
        NightChanged = true;
        _nightText.Text = path != null ? $"夜: {System.IO.Path.GetFileName(path)}" : "夜の画像: なし";
        if (px == null) _showDay.IsChecked = true;
        Redraw();
        return true;
    }

    private void CommitInterval()
    {
        if (int.TryParse(_interval.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) SetInterval(v);
        else _interval.Text = Interval.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>1 コマの高さを変える（1〜中身の高さ）。</summary>
    public void SetInterval(int v)
    {
        Interval = Math.Clamp(v, 1, _contentHeight);
        _interval.Text = Interval.ToString(CultureInfo.InvariantCulture);
        int frames = Math.Max(1, FrameCount);
        _value.Maximum = frames - 1;
        if (_value.Value > _value.Maximum) _value.Value = _value.Maximum;
        var (n, rem) = DigitStrip.Count(_contentHeight, Interval);
        _info.Text = $"画像の高さ {_contentHeight} px ÷ 1 コマ {Interval} px = {n} コマ" + (rem != 0 ? $"（{rem} px 余ります。区切りがずれていないか確かめてください）" : "")
            + (_detectNote.Length > 0 ? $"\n{_detectNote}" : "");
        _info.Foreground = rem != 0 ? Brushes.DarkOrange : Brushes.DimGray;
        Redraw();
    }

    /// <summary>画像から 1 コマの高さを求め直す。</summary>
    private void Redetect()
    {
        var found = DigitalFrames.Detect(_day, _contentHeight);
        if (found == null) { _detectNote = "画像から 1 コマの高さを求められませんでした"; SetInterval(Interval); return; }
        _detectNote = $"画像から求めた値です（{(found.Confident ? "確定" : "推定")}: {found.Reason}）";
        SetInterval(found.Interval);
    }

    /// <summary>今の値（コマの番号）。</summary>
    public int Value { get => (int)_value.Value; set => _value.Value = Math.Clamp(value, 0, (int)_value.Maximum); }

    private void Redraw()
    {
        _dnSwitch.IsEnabled = _nightBmp != null;
        bool night = _showNight.IsChecked == true && _nightBmp != null;
        var bmp = night ? _nightBmp! : _dayBmp;
        int v = (int)_value.Value;
        _valueText.Text = $"{v}（{Math.Max(1, FrameCount)} コマ中）";
        _strip.Set(bmp, _contentHeight, Interval, v);
        _frame.Set(bmp, Interval, v);
    }

    /// <summary>全体のコマ割: 画像・区切りの赤い線・今のコマの黄色い枠・コマの番号。</summary>
    internal sealed class StripView : FrameworkElement
    {
        private BitmapSource? _bmp;
        private int _content, _interval, _current;
        private double _scale = 1;
        private static readonly Pen Line = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0x40, 0x40)), 1));
        private static readonly Pen Current = Freeze(new Pen(Brushes.Yellow, 2));
        private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

        public StripView() => RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);

        public void Set(BitmapSource bmp, int content, int interval, int current)
        {
            _bmp = bmp; _content = content; _interval = Math.Max(1, interval); _current = current;
            // 細い画像は大きく、長い画像は小さく（縦 4000px まで）
            _scale = Math.Clamp(Math.Min(220.0 / Math.Max(1, bmp.PixelWidth), 4000.0 / Math.Max(1, bmp.PixelHeight)), 0.1, 8);
            Width = bmp.PixelWidth * _scale + 44;
            Height = bmp.PixelHeight * _scale + 8;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_bmp == null) return;
            double w = _bmp.PixelWidth * _scale, x0 = 30, y0 = 4;
            dc.DrawRectangle(Checker, null, new Rect(x0, y0, w, _bmp.PixelHeight * _scale));
            dc.DrawImage(_bmp, new Rect(x0, y0, w, _bmp.PixelHeight * _scale));
            // 中身より下（2 の累乗に広げた所）は暗くする
            if (_content < _bmp.PixelHeight)
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)), null, new Rect(x0, y0 + _content * _scale, w, (_bmp.PixelHeight - _content) * _scale));
            int n = _content / _interval;
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            for (int i = 0; i <= n; i++)
            {
                double y = y0 + i * _interval * _scale;
                dc.DrawLine(Line, new Point(x0 - 4, y), new Point(x0 + w + 4, y));
                if (i < n && _interval * _scale >= 10)
                {
                    var t = new FormattedText(i.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Yu Gothic UI"), Math.Min(12, _interval * _scale * 0.8), Brushes.White, dpi);
                    dc.DrawText(t, new Point(x0 - 6 - t.Width, y + (_interval * _scale - t.Height) / 2));
                }
            }
            if (_current >= 0 && _current < n) dc.DrawRectangle(null, Current, new Rect(x0 - 1, y0 + _current * _interval * _scale - 1, w + 2, _interval * _scale + 2));
        }
    }

    /// <summary>BVE での見え方: 今の値のコマだけ（大きくして、透明な所は市松模様）。</summary>
    private sealed class FrameView : FrameworkElement
    {
        private BitmapSource? _bmp;
        private int _interval, _current;

        public FrameView() => RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);

        public void Set(BitmapSource bmp, int interval, int current)
        {
            _bmp = bmp; _interval = Math.Max(1, interval); _current = current;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_bmp == null || ActualWidth <= 0 || ActualHeight <= 0) return;
            int top = _current * _interval;
            if (top >= _bmp.PixelHeight) return;
            int h = Math.Min(_interval, _bmp.PixelHeight - top);
            var crop = new CroppedBitmap(_bmp, new Int32Rect(0, top, _bmp.PixelWidth, h));
            double s = Math.Max(0.1, Math.Min(8, Math.Min((ActualWidth - 16) / _bmp.PixelWidth, (ActualHeight - 16) / _interval)));
            double w = _bmp.PixelWidth * s, hh = _interval * s;
            var r = new Rect((ActualWidth - w) / 2, (ActualHeight - hh) / 2, w, hh);
            dc.DrawRectangle(Checker, new Pen(Brushes.Gray, 1), r);
            dc.DrawImage(crop, new Rect(r.X, r.Y, w, h * s));
        }
    }

    /// <summary>透明な所を見せる市松模様。</summary>
    private static readonly Brush Checker = MakeChecker();

    private static Brush MakeChecker()
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)), null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x68, 0x68, 0x68)), null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x68, 0x68, 0x68)), null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        var b = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
        b.Freeze();
        return b;
    }
}
