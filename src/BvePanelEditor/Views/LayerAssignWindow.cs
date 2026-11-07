using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BvePanelEditor.Core.Crop;
using BvePanelEditor.Core.Psd;
using BvePanelEditor.Rendering;
using Microsoft.Win32;

namespace BvePanelEditor.Views;

/// <summary>振り分けの結果 1 欄ぶん。Pixels は選んだレイヤーをキャンバス大に重ねた絵。</summary>
public sealed record LayerPick(string PsdPath, List<string> Layers, PsdPixels Pixels);

/// <summary>
/// PSD のレイヤーを、画像からパネルを作るの各欄（昼・無発光など）に振り分ける画面。
/// 1 つの欄は 1 つの PSD から作る（ほかの PSD のレイヤーにチェックすると、その欄はそちらの PSD に移る）。
/// </summary>
public sealed class LayerAssignWindow : Window
{
    private readonly List<PsdFile> _psds = [];
    private readonly string[] _columns;
    private readonly CropSlotKind[] _kinds;
    private readonly int[] _colPsd;                    // 欄ごとの PSD（-1 = なし）
    private readonly HashSet<string>[] _colLayers;     // 欄ごとの画像レイヤーのパス
    private int _shown;                                // 一覧に出している PSD
    private int _previewCol;
    private bool _suppress;

    private readonly ComboBox _psdCombo = new() { MinWidth = 320, Margin = new Thickness(6, 0, 6, 0) };
    private readonly StackPanel _rows = new();
    private readonly Grid _header = new() { Margin = new Thickness(0, 4, 0, 2) };
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _previewInfo = new() { Foreground = Brushes.DimGray, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly List<(CheckBox Box, PsdLayer Layer, int Col)> _checks = [];
    private readonly List<RadioButton> _heads = [];
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };

    /// <summary>［OK］で決まった欄ごとの結果。何も選ばなかった欄は null。</summary>
    public LayerPick?[]? Result { get; private set; }

    /// <param name="psdPaths">最初に読む PSD。</param>
    /// <param name="columnPsd">欄ごとに使う PSD（psdPaths のどれか、なければ null）。</param>
    /// <param name="columnLayers">欄ごとのレイヤー。null なら名前から振り分ける。</param>
    /// <param name="subject">何の画像を作るか（題名に出す。例: 重ねる画像の名前）。</param>
    public LayerAssignWindow(Window owner, IReadOnlyList<string> psdPaths, string[] columns, CropSlotKind[] kinds,
        IReadOnlyList<string?> columnPsd, IReadOnlyList<List<string>?> columnLayers, string? subject = null)
    {
        Owner = owner;
        Title = "PSD のレイヤーを振り分ける" + (string.IsNullOrWhiteSpace(subject) ? "" : $" - {subject}");
        Width = 1250; Height = 860;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Yu Gothic UI");
        FontSize = 12;
        Background = SystemColors.WindowBrush;
        _columns = columns;
        _kinds = kinds;
        _colPsd = Enumerable.Repeat(-1, columns.Length).ToArray();
        _colLayers = columns.Select(_ => new HashSet<string>()).ToArray();
        foreach (var p in psdPaths) AddPsd(p, showError: true);

        // 最初の振り分け
        for (int k = 0; k < columns.Length; k++)
        {
            if (columnPsd[k] is not { } cp) continue;
            int i = _psds.FindIndex(x => SamePath(x.FilePath, cp));
            if (i < 0) continue;
            _colPsd[k] = i;
            if (columnLayers[k] is { } given) _colLayers[k].UnionWith(given);
        }
        for (int i = 0; i < _psds.Count; i++)
        {
            var cols = Enumerable.Range(0, columns.Length).Where(k => _colPsd[k] == i && columnLayers[k] == null).ToList();
            if (cols.Count > 0) ApplyGuess(i, cols);
        }
        _previewCol = Enumerable.Range(0, columns.Length).FirstOrDefault(k => _colPsd[k] >= 0);
        _shown = _previewCol < columns.Length && _colPsd[_previewCol] >= 0 ? _colPsd[_previewCol] : 0;

        Content = BuildUi();
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); RenderPreview(); };
        Loaded += (_, _) => { RefreshPsdCombo(); ShowPsd(_shown); RenderPreview(); };
        Closed += (_, _) => { foreach (var p in _psds) p.Dispose(); };
    }

    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private bool AddPsd(string path, bool showError)
    {
        if (_psds.Any(p => SamePath(p.FilePath, path))) return true;
        try
        {
            _psds.Add(PsdFile.Open(path));
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            if (showError) MessageBox.Show(Owner ?? this, $"PSD を読めませんでした:\n{path}\n{e.Message}", "パネルくん", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    /// <summary>名前から振り分ける（PSD i を cols の欄に使う）。何も選べなかった欄は空にする。</summary>
    private void ApplyGuess(int i, List<int> cols)
    {
        var g = CropLayers.Guess(_psds[i], cols.Select(k => _kinds[k]).ToList());
        for (int j = 0; j < cols.Count; j++)
        {
            int k = cols[j];
            _colLayers[k].Clear();
            if (g[j].Count == 0) { _colPsd[k] = -1; continue; }
            _colPsd[k] = i;
            _colLayers[k].UnionWith(g[j]);
        }
    }

    // ================= 画面 =================

    private UIElement BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(8) };

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        top.Children.Add(new TextBlock { Text = "PSD", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
        top.Children.Add(_psdCombo);
        var add = new Button { Content = "PSD を足す…", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0) };
        add.Click += (_, _) => OnAddPsd();
        top.Children.Add(add);
        var guess = new Button
        {
            Content = "名前から振り分け直す", Padding = new Thickness(8, 2, 8, 2),
            ToolTip = "この PSD を使っている欄を、レイヤー名（発光・光・点灯・_TT／夜・night・_N）と表示・非表示から振り分け直します",
        };
        guess.Click += (_, _) => OnGuess();
        top.Children.Add(guess);
        var clear = new Button
        {
            Content = "チェックをすべて外す", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "この PSD を使っている欄のチェックを、すべて外します（そのあと、使うレイヤーだけにチェックできます）",
        };
        clear.Click += (_, _) => ClearChecks();
        top.Children.Add(clear);
        top.Children.Add(new TextBlock
        {
            Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap,
            Text = "チェックしたレイヤーを重ねて、その欄の画像にします（PSD の表示・非表示は関係ありません）。1 つの欄は 1 つの PSD から作ります。",
        });
        _psdCombo.SelectionChanged += (_, _) => { if (!_suppress && _psdCombo.SelectedIndex >= 0) ShowPsd(_psdCombo.SelectedIndex); };
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var bottom = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var ok = new Button { Content = "OK", Width = 100, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => OnOk();
        var cancel = new Button { Content = "キャンセル", Width = 100, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 400 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(440), MinWidth = 240 });

        var left = new DockPanel();
        BuildHeader();
        DockPanel.SetDock(_header, Dock.Top);
        left.Children.Add(_header);
        left.Children.Add(new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(left);

        var split = new GridSplitter { HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(split, 1);
        body.Children.Add(split);

        var right = new DockPanel { Margin = new Thickness(4, 0, 0, 0) };
        var ptitle = new TextBlock { Text = "見本（上の欄の丸で切り替え）", Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(ptitle, Dock.Top);
        right.Children.Add(ptitle);
        DockPanel.SetDock(_previewInfo, Dock.Bottom);
        right.Children.Add(_previewInfo);
        right.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2E)), Child = _preview, Padding = new Thickness(4) });
        Grid.SetColumn(right, 2);
        body.Children.Add(right);
        root.Children.Add(body);
        return root;
    }

    private const double ColWidth = 92;

    private void BuildHeader()
    {
        _header.ColumnDefinitions.Clear();
        for (int k = 0; k < _columns.Length; k++) _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColWidth) });
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int k = 0; k < _columns.Length; k++)
        {
            int col = k;
            var rb = new RadioButton { GroupName = "preview", IsChecked = k == _previewCol, Margin = new Thickness(0, 0, 4, 0), ToolTip = "右に見本を出す欄" };
            rb.Checked += (_, _) => { if (_suppress) return; _previewCol = col; SchedulePreview(); };
            Grid.SetColumn(rb, k);
            _header.Children.Add(rb);
            _heads.Add(rb);
        }
        var name = new TextBlock { Text = "レイヤー（上が手前）", VerticalAlignment = VerticalAlignment.Bottom, Foreground = Brushes.DimGray, Margin = new Thickness(4, 0, 0, 0) };
        Grid.SetColumn(name, _columns.Length);
        _header.Children.Add(name);
        RefreshHeader();
    }

    private void RefreshHeader()
    {
        for (int k = 0; k < _heads.Count; k++)
        {
            var src = _colPsd[k] >= 0 ? Path.GetFileName(_psds[_colPsd[k]].FilePath) : "（なし）";
            var other = _colPsd[k] >= 0 && _colPsd[k] != _shown;
            _heads[k].Content = new TextBlock
            {
                Text = $"{_columns[k]}\n{src}" + (_colPsd[k] >= 0 ? $"\n{_colLayers[k].Count} 個" : ""),
                FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = other ? Brushes.Gray : Brushes.Black,
                FontWeight = k == _previewCol ? FontWeights.SemiBold : FontWeights.Normal,
            };
            _heads[k].ToolTip = other ? $"この欄はほかの PSD（{src}）から作ります。ここでチェックすると、この PSD に切り替わります" : "右に見本を出す欄";
        }
    }

    private void RefreshPsdCombo()
    {
        _suppress = true;
        _psdCombo.ItemsSource = _psds.Select(p => $"{Path.GetFileName(p.FilePath)}（{p.Width}×{p.Height}、{p.Layers.Count(l => l.Kind != PsdLayerKind.FolderEnd)} レイヤー）").ToList();
        _psdCombo.SelectedIndex = _shown;
        _suppress = false;
    }

    private void ShowPsd(int i)
    {
        if (i < 0 || i >= _psds.Count) return;
        _shown = i;
        var psd = _psds[i];
        _rows.Children.Clear();
        _checks.Clear();
        foreach (var l in Enumerable.Reverse(psd.Layers))
        {
            if (l.Kind == PsdLayerKind.FolderEnd) continue;
            var row = new Grid { Height = 22 };
            for (int k = 0; k < _columns.Length; k++) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColWidth) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bool usable = l.IsFolder || l.CanCompose;
            if (usable)
                for (int k = 0; k < _columns.Length; k++)
                {
                    int col = k;
                    var layer = l;
                    var cb = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0), IsThreeState = false };
                    cb.Click += (_, _) => OnCheck(layer, col);
                    Grid.SetColumn(cb, k);
                    row.Children.Add(cb);
                    _checks.Add((cb, l, k));
                }
            int depth = 0;
            for (var p = l.Parent; p != null; p = p.Parent) depth++;
            var label = new TextBlock
            {
                Text = (l.IsFolder ? "▸ " : "") + l.Name + (l.EffectivelyHidden ? "（非表示）" : "") + (usable ? "" : "（重ねられません）"),
                Margin = new Thickness(4 + depth * 16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Foreground = !usable ? Brushes.Silver : l.EffectivelyHidden ? Brushes.Gray : Brushes.Black,
                FontWeight = l.IsFolder ? FontWeights.SemiBold : FontWeights.Normal,
                ToolTip = l.Path + (PsdImport.Notes(l) is { Length: > 0 } n ? "\n" + n : ""),
            };
            Grid.SetColumn(label, _columns.Length);
            row.Children.Add(label);
            _rows.Children.Add(row);
        }
        RefreshChecks();
        RefreshHeader();
        if (_psdCombo.SelectedIndex != i) { _suppress = true; _psdCombo.SelectedIndex = i; _suppress = false; }
    }

    private IEnumerable<PsdLayer> PixelLayersOf(PsdLayer l) =>
        l.IsFolder ? _psds[_shown].ComposableLayers.Where(x => IsInside(x, l)) : l.CanCompose ? [l] : [];

    private static bool IsInside(PsdLayer l, PsdLayer folder)
    {
        for (var p = l.Parent; p != null; p = p.Parent) if (p == folder) return true;
        return false;
    }

    private void RefreshChecks()
    {
        _suppress = true;
        foreach (var (box, layer, col) in _checks)
        {
            if (_colPsd[col] != _shown) { box.IsChecked = false; box.Opacity = 0.55; continue; }
            box.Opacity = 1;
            var inner = PixelLayersOf(layer).ToList();
            int on = inner.Count(x => _colLayers[col].Contains(x.Path));
            box.IsChecked = inner.Count > 0 && on == inner.Count ? true : on == 0 ? false : null;
        }
        _suppress = false;
        int total = Enumerable.Range(0, _columns.Length).Count(k => _colPsd[k] >= 0);
        _status.Text = $"{total} / {_columns.Length} 欄に PSD を振り分けています。チェックのない欄は、今の画像のまま変えません。";
    }

    private void OnCheck(PsdLayer layer, int col)
    {
        if (_suppress) return;
        if (_colPsd[col] != _shown)
        {
            // ほかの PSD から作っていた欄を、この PSD に切り替える
            _colPsd[col] = _shown;
            _colLayers[col].Clear();
        }
        var inner = PixelLayersOf(layer).Select(x => x.Path).ToList();
        bool allOn = inner.All(_colLayers[col].Contains);
        if (allOn) _colLayers[col].ExceptWith(inner); else _colLayers[col].UnionWith(inner);
        if (_colLayers[col].Count == 0) _colPsd[col] = -1;
        RefreshChecks();
        RefreshHeader();
        if (col == _previewCol) SchedulePreview();
    }

    private void OnAddPsd()
    {
        var dlg = new OpenFileDialog { AddToRecent = false, Title = "PSD を足す", Filter = "Photoshop|*.psd" };
        if (_psds.Count > 0) dlg.InitialDirectory = Path.GetDirectoryName(_psds[0].FilePath);
        if (dlg.ShowDialog(this) != true || !AddPsd(dlg.FileName, showError: true)) return;
        RefreshPsdCombo();
        ShowPsd(_psds.FindIndex(p => SamePath(p.FilePath, dlg.FileName)));
    }

    /// <summary>今出している PSD を使っている欄のチェックを、すべて外す。</summary>
    public void ClearChecks()
    {
        for (int k = 0; k < _columns.Length; k++)
        {
            if (_colPsd[k] != _shown) continue;
            _colLayers[k].Clear();
            _colPsd[k] = -1;
        }
        RefreshChecks();
        RefreshHeader();
        SchedulePreview();
    }

    /// <summary>確認用: 欄ごとのチェックしたレイヤー。</summary>
    public List<string>[] CheckedForTest => _colLayers.Select(s => s.ToList()).ToArray();

    private void OnGuess()
    {
        var cols = Enumerable.Range(0, _columns.Length).Where(k => _colPsd[k] == _shown).ToList();
        if (cols.Count == 0) cols = Enumerable.Range(0, _columns.Length).Where(k => _colPsd[k] < 0).ToList();
        if (cols.Count == 0)
        {
            MessageBox.Show(this, "どの欄もほかの PSD から作っています。この PSD に切り替えたい欄のレイヤーにチェックしてください。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ApplyGuess(_shown, cols);
        RefreshChecks();
        RefreshHeader();
        SchedulePreview();
    }

    private void SchedulePreview()
    {
        RefreshHeader();
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void RenderPreview()
    {
        int k = _previewCol;
        if (k < 0 || k >= _columns.Length || _colPsd[k] < 0)
        {
            _preview.Source = null;
            _previewInfo.Text = k >= 0 && k < _columns.Length ? $"{_columns[k]}: レイヤーが選ばれていません" : "";
            return;
        }
        var psd = _psds[_colPsd[k]];
        var layers = psd.ComposableLayers.Where(l => _colLayers[k].Contains(l.Path)).ToList();
        int scale = Math.Max(1, (int)Math.Ceiling(Math.Max(psd.Width, psd.Height) / 900.0));
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var px = PsdCompose.Compose(psd, layers, psd.Canvas, scale);
            _preview.Source = ImageLoader.ToBitmap(px);
            _previewInfo.Text = $"{_columns[k]}: {Path.GetFileName(psd.FilePath)} のレイヤー {layers.Count} 個を重ねたもの（縮小表示）";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException)
        {
            _preview.Source = null;
            _previewInfo.Text = $"見本を作れませんでした: {e.Message}";
        }
        finally { Mouse.OverrideCursor = null; }
    }

    private void OnOk()
    {
        var result = new LayerPick?[_columns.Length];
        var warnings = new List<string>();
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            for (int k = 0; k < _columns.Length; k++)
            {
                if (_colPsd[k] < 0 || _colLayers[k].Count == 0) continue;
                var psd = _psds[_colPsd[k]];
                var layers = psd.ComposableLayers.Where(l => _colLayers[k].Contains(l.Path)).OrderBy(l => l.Index).Select(l => l.Path).ToList();
                var w = new List<string>();
                var px = CropLayers.Compose(psd, layers, w);
                warnings.AddRange(w.Select(x => $"{_columns[k]}: {x}"));
                result[k] = new LayerPick(psd.FilePath, layers, px);
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException)
        {
            Mouse.OverrideCursor = null;
            MessageBox.Show(this, $"レイヤーを重ねられませんでした:\n{e.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally { Mouse.OverrideCursor = null; }
        if (warnings.Count > 0)
            MessageBox.Show(this, "次の点に注意してください。\n\n" + string.Join("\n", warnings.Distinct().Take(12).Select(x => "・" + x)), Title, MessageBoxButton.OK, MessageBoxImage.Information);
        Result = result;
        DialogResult = true;
    }

    // ================= 確認用 =================

    /// <summary>確認用: 今の振り分け（欄ごとの PSD のファイル名とレイヤー数）。</summary>
    public string DescribeForTest() => string.Join(" / ", Enumerable.Range(0, _columns.Length)
        .Select(k => $"{_columns[k]}={(_colPsd[k] >= 0 ? Path.GetFileName(_psds[_colPsd[k]].FilePath) + ":" + _colLayers[k].Count : "なし")}"));

    /// <summary>確認用: ［OK］と同じ結果を作る（窓は閉じない）。</summary>
    public LayerPick?[] PickForTest()
    {
        var result = new LayerPick?[_columns.Length];
        for (int k = 0; k < _columns.Length; k++)
        {
            if (_colPsd[k] < 0 || _colLayers[k].Count == 0) continue;
            var psd = _psds[_colPsd[k]];
            var layers = psd.ComposableLayers.Where(l => _colLayers[k].Contains(l.Path)).OrderBy(l => l.Index).Select(l => l.Path).ToList();
            result[k] = new LayerPick(psd.FilePath, layers, CropLayers.Compose(psd, layers));
        }
        return result;
    }
}
