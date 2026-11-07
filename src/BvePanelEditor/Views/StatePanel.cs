using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BvePanelEditor.Core;
using BvePanelEditor.Rendering;

namespace BvePanelEditor.Views;

/// <summary>状態量 1 つぶんの値の範囲。</summary>
public sealed class StateRange
{
    public string Key = "";
    public string Label = "";
    public double Min, Max = 1;
    public bool IsBool;
    public bool IsInteger;
}

/// <summary>状態量（速度・圧力・ATS 値など）を手で動かしてプレビューする欄。</summary>
public sealed class StatePanel
{
    private readonly StackPanel _host;
    private readonly Dictionary<string, double> _state;
    private readonly Dictionary<string, Action<double>> _setters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Border> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _marks = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _highlightKeys = new(StringComparer.OrdinalIgnoreCase);
    private string _highlightLabel = "";

    private static readonly Brush HighlightFill = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xF1, 0xB8)));
    private static readonly Brush HighlightEdge = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0x9A, 0x00)));
    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
    private List<StateRange> _ranges = [];
    private string _signature = "";
    private bool _updating;
    private double _sweepTime;

    public event Action? Changed;

    public StatePanel(StackPanel host, Dictionary<string, double> state)
    {
        _host = host;
        _state = state;
    }

    public IReadOnlyList<StateRange> Ranges => _ranges;

    /// <summary>色を付けている状態量のキー。</summary>
    public string? HighlightedKey => _highlightKeys.FirstOrDefault();
    public IReadOnlyCollection<string> HighlightedKeys => _highlightKeys;

    public StateRange? Range(string key) => _ranges.FirstOrDefault(r => r.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>1 つの状態量の値を変える（スライダーなどの表示も合わせる）。</summary>
    public void SetValue(string key, double value)
    {
        _state[key] = value;
        if (_setters.TryGetValue(key, out var set)) set(value);
        Changed?.Invoke();
    }

    /// <summary>選んだ計器の状態量の行に色を付ける。空なら色を消す。</summary>
    public void Highlight(IEnumerable<string> keys, string label)
    {
        _highlightKeys = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        _highlightLabel = label;
        ApplyHighlight(scroll: true);
    }

    private void ApplyHighlight(bool scroll)
    {
        bool first = true;
        foreach (var (k, row) in _rows)
        {
            bool on = _highlightKeys.Contains(k);
            row.Background = on ? HighlightFill : Brushes.Transparent;
            row.BorderBrush = on ? HighlightEdge : Brushes.Transparent;
            if (_marks.TryGetValue(k, out var mark))
            {
                mark.Text = on ? $"◀ 選択中: {_highlightLabel}" : "";
                mark.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            }
            if (on && scroll && first) { row.BringIntoView(); first = false; }
        }
    }

    /// <summary>パネルで使われている状態量から欄を作る（同じ顔ぶれなら作り直さない）。</summary>
    public void Update(PanelScene? scene)
    {
        var ranges = scene == null ? [] : ComputeRanges(scene);
        var sig = string.Join("|", ranges.Select(r => $"{r.Key}:{r.Min}:{r.Max}:{r.IsBool}"));
        if (sig == _signature) return;
        _signature = sig;
        _ranges = ranges;
        Build();
    }

    public static List<StateRange> ComputeRanges(PanelScene scene)
    {
        var map = new Dictionary<string, StateRange>(StringComparer.OrdinalIgnoreCase);
        var onlyLamps = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in scene.Items)
        {
            var key = Subjects.BaseKey(it.Subject);
            if (key == null) continue;
            var info = Subjects.Info(key);
            bool isAts = Subjects.IsAnyAts(key, out _);
            if (!map.TryGetValue(key, out var r))
            {
                r = new StateRange { Key = key, Label = info.Label, IsBool = info.IsBool };
                if (isAts) { r.Min = 0; r.Max = 1; }
                else { r.Min = info.Min; r.Max = info.Max; }
                r.IsInteger = isAts || info.IsBool || key is "power" or "brake" or "sec";
                map[key] = r;
                onlyLamps[key] = true;
            }
            if (it.Kind != SectionKind.PilotLamp) onlyLamps[key] = false;
            switch (it.Kind)
            {
                case SectionKind.Needle:
                case SectionKind.DigitalGauge:
                    if (Math.Abs(it.Maximum - it.Minimum) > 1e-12)
                    {
                        r.Min = Math.Min(r.Min, Math.Min(it.Minimum, it.Maximum));
                        r.Max = Math.Max(isAts && r.Max == 1 ? 0 : r.Max, Math.Max(it.Minimum, it.Maximum));
                    }
                    break;
                case SectionKind.DigitalNumber:
                    if (!it.Subject.Contains('d', StringComparison.OrdinalIgnoreCase) || isAts)
                        r.Max = Math.Max(r.Max, it.Frames - 1);
                    break;
            }
        }
        foreach (var r in map.Values)
        {
            if (Subjects.IsAnyAts(r.Key, out _) && onlyLamps[r.Key]) r.IsBool = true;
            if (r.IsBool) { r.Min = 0; r.Max = 1; r.IsInteger = true; }
        }
        return map.Values
            .OrderBy(r => Subjects.IsAnyAts(r.Key, out var n) ? 1000 + n : Array.FindIndex(Subjects.Standard, s => s.Key == r.Key) is var i and >= 0 ? i : 500)
            .ToList();
    }

    private void Build()
    {
        _host.Children.Clear();
        _setters.Clear();
        _rows.Clear();
        _marks.Clear();
        if (_ranges.Count == 0)
        {
            _host.Children.Add(new TextBlock { Text = "このパネルでは状態量を使っていません。", Foreground = Brushes.Gray, Margin = new Thickness(4) });
            return;
        }
        foreach (var r in _ranges)
        {
            if (!_state.ContainsKey(r.Key)) _state[r.Key] = r.Key == "door" ? 1 : Math.Clamp(0, r.Min, r.Max);
            var range = r;
            if (r.IsBool)
            {
                var cb = new CheckBox
                {
                    Content = $"{r.Key}  {r.Label}", IsChecked = _state[r.Key] != 0, Margin = new Thickness(4, 3, 4, 3),
                };
                cb.Click += (_, _) => { _state[range.Key] = cb.IsChecked == true ? 1 : 0; Changed?.Invoke(); };
                _setters[r.Key] = v => cb.IsChecked = v != 0;
                AddRow(r.Key, cb);
                continue;
            }

            var grid = new Grid { Margin = new Thickness(4, 2, 4, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition());
            var label = new TextBlock { Text = $"{r.Key}  {r.Label}", FontSize = 11 };
            Grid.SetColumnSpan(label, 2);
            var slider = new Slider
            {
                Minimum = r.Min, Maximum = r.Max, Value = _state[r.Key],
                IsSnapToTickEnabled = r.IsInteger, TickFrequency = r.IsInteger ? 1 : (r.Max - r.Min) / 100,
                SmallChange = r.IsInteger ? 1 : (r.Max - r.Min) / 100, LargeChange = r.IsInteger ? 1 : (r.Max - r.Min) / 10,
            };
            var box = new TextBox { Text = Fmt(_state[r.Key]), Margin = new Thickness(4, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
            Grid.SetRow(slider, 1);
            Grid.SetRow(box, 1);
            Grid.SetColumn(box, 1);
            grid.Children.Add(label);
            grid.Children.Add(slider);
            grid.Children.Add(box);
            slider.ValueChanged += (_, e) =>
            {
                if (_updating) return;
                _state[range.Key] = e.NewValue;
                _updating = true;
                box.Text = Fmt(e.NewValue);
                _updating = false;
                Changed?.Invoke();
            };
            void ApplyBox()
            {
                if (PanelValues.TryDouble(box.Text, out var v))
                {
                    _state[range.Key] = v;
                    _updating = true;
                    slider.Value = Math.Clamp(v, slider.Minimum, slider.Maximum);
                    _updating = false;
                    Changed?.Invoke();
                }
            }
            box.LostKeyboardFocus += (_, _) => ApplyBox();
            box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) ApplyBox(); };
            _setters[r.Key] = v =>
            {
                _updating = true;
                slider.Value = Math.Clamp(v, slider.Minimum, slider.Maximum);
                if (!box.IsKeyboardFocusWithin) box.Text = Fmt(v);
                _updating = false;
            };
            AddRow(r.Key, grid);
        }
        ApplyHighlight(scroll: false);
    }

    /// <summary>1 行を色付けできる枠で包んで並べる。</summary>
    private void AddRow(string key, FrameworkElement content)
    {
        var mark = new TextBlock { Foreground = HighlightEdge, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 4, 2), Visibility = Visibility.Collapsed, TextTrimming = TextTrimming.CharacterEllipsis };
        var stack = new StackPanel();
        stack.Children.Add(content);
        stack.Children.Add(mark);
        var border = new Border { Child = stack, BorderThickness = new Thickness(4, 0, 0, 0), BorderBrush = Brushes.Transparent, Margin = new Thickness(0, 1, 0, 1), CornerRadius = new CornerRadius(2) };
        _rows[key] = border;
        _marks[key] = mark;
        _host.Children.Add(border);
    }

    private static string Fmt(double v) => PanelValues.Num(Math.Round(v, 2));

    public void SetAll(Func<StateRange, double> value)
    {
        foreach (var r in _ranges)
        {
            var v = value(r);
            _state[r.Key] = v;
            if (_setters.TryGetValue(r.Key, out var set)) set(v);
        }
        Changed?.Invoke();
    }

    public void Reset() => SetAll(r => r.Key == "door" ? 1 : Math.Clamp(0, r.Min, r.Max));

    /// <summary>全部の値を最小〜最大でゆっくり往復させる。</summary>
    public void Sweep(double dt)
    {
        _sweepTime += dt;
        const double period = 6;
        double t = 0.5 - 0.5 * Math.Cos(2 * Math.PI * _sweepTime / period);
        SetAll(r =>
        {
            if (r.IsBool) return t > 0.5 ? 1 : 0;
            double v = r.Min + (r.Max - r.Min) * t;
            return r.IsInteger ? Math.Round(v) : v;
        });
    }
}
