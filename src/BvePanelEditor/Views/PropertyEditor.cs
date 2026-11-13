using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BvePanelEditor.Core;
using Microsoft.Win32;

namespace BvePanelEditor.Views;

/// <summary>選んだセクションのキーを一覧で編集する欄。</summary>
public sealed class PropertyEditor
{
    private readonly StackPanel _host;
    private readonly Window _owner;
    private readonly Dictionary<string, Func<string>> _getters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Action<string>> _setters = new(StringComparer.OrdinalIgnoreCase);
    private PanelSection? _section;
    private string _builtSignature = "";

    /// <summary>キーの値を変えたい（null または空で削除）。</summary>
    public event Action<string, string?>? Commit;

    /// <summary>名前（直前のコメント）を変えたい。</summary>
    public event Action<string>? TitleCommit;

    /// <summary>グループ名を変えたい。</summary>
    public event Action<string>? GroupRenameCommit;
    /// <summary>グループの位置（いちばん左上）を変えたい。</summary>
    public event Action<double, double>? GroupLocationCommit;
    public event Action? UngroupRequested;
    public event Action? DuplicateGroupRequested;
    public event Action? MakeGroupRequested;
    /// <summary>複数のオブジェクトの同じキーをまとめて書き換えたい（キー、値。空なら行を消す）。</summary>
    public event Action<string, string>? MultiCommit;
    /// <summary>整列したい。</summary>
    public event Action<AlignKind>? AlignRequested;
    /// <summary>レイヤーを上げる・下げる。</summary>
    public event Action<LayerOp>? LayerRequested;

    /// <summary>次に Show したとき、同じ選択でも欄を作り直す。</summary>
    public void Invalidate() => _builtSignature = null;

    /// <summary>レイヤー（重なりの順）を動かすボタン。今のレイヤーの範囲も出す。</summary>
    private void AddLayerButtons(IReadOnlyList<PanelSection> sections)
    {
        var withLayer = sections.Where(x => x.Kind is SectionKind.PilotLamp or SectionKind.Needle or SectionKind.DigitalNumber or SectionKind.DigitalGauge).ToList();
        if (withLayer.Count == 0) return;
        var layers = withLayer.Select(x => (int)Math.Round(PanelValues.Double(x.Get("Layer"), 0))).ToList();
        int lo = layers.Min(), hi = layers.Max();
        _host.Children.Add(new TextBlock
        {
            Text = $"レイヤー（重なりの順）　今: {(lo == hi ? $"{lo}" : $"{lo}〜{hi}")}",
            Foreground = Brushes.DimGray, Margin = new Thickness(4, 10, 4, 2),
            ToolTip = "数が大きいほど手前に描かれます。選んだオブジェクトどうしの上下の順は変えずに動かします",
        });
        var row = new WrapPanel { Margin = new Thickness(4, 0, 4, 4) };
        row.Children.Add(MakeButton("最背面へ", () => LayerRequested?.Invoke(LayerOp.Back)));
        row.Children.Add(MakeButton("1 つ下げる", () => LayerRequested?.Invoke(LayerOp.Down)));
        row.Children.Add(MakeButton("1 つ上げる", () => LayerRequested?.Invoke(LayerOp.Up)));
        row.Children.Add(MakeButton("最前面へ", () => LayerRequested?.Invoke(LayerOp.Front)));
        foreach (var b in row.Children.OfType<Button>())
            b.ToolTip = (string)b.Content switch
            {
                "最背面へ" => "ほかのどのオブジェクトよりも奥へ（Ctrl+Shift+[）",
                "1 つ下げる" => "レイヤーを 1 つ小さく（Ctrl+[）",
                "1 つ上げる" => "レイヤーを 1 つ大きく（Ctrl+]）",
                _ => "ほかのどのオブジェクトよりも手前へ（Ctrl+Shift+]）",
            };
        _host.Children.Add(row);
        // 選んだオブジェクトのレイヤーをそろえる（そろえる先は、最初に選んだオブジェクトのレイヤー・今使っているレイヤーから選ぶか、数を入れる）
        if (withLayer.Count > 1)
        {
            int first = PrimaryLayer ?? layers[0];
            var box = new ComboBox
            {
                IsEditable = true, Width = 64, Margin = new Thickness(0, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center,
                ItemsSource = layers.Append(first).Distinct().OrderByDescending(x => x).Select(x => x.ToString()).ToList(),
                Text = first.ToString(),
                ToolTip = "そろえる先のレイヤー。初めは最初に選んだオブジェクトのレイヤー。▼ で今使っているレイヤーから選ぶか、数を入れます（0 以上）",
            };
            var unify = MakeButton("このレイヤーにそろえる", () =>
            {
                if (int.TryParse(box.Text.Trim(), out var to) && to >= 0) UnifyLayerRequested?.Invoke(to);
                else box.Text = first.ToString();
            });
            unify.ToolTip = "選んだオブジェクトのレイヤーを、左の欄のレイヤーにそろえます。重なっているオブジェクトの手前・奥が入れ替わるときは確認します";
            var r2 = new WrapPanel { Margin = new Thickness(4, 0, 4, 4) };
            r2.Children.Add(new TextBlock { Text = "そろえる先", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            r2.Children.Add(box);
            r2.Children.Add(unify);
            _host.Children.Add(r2);
            _host.Children.Add(new TextBlock
            {
                Text = $"最初に選んだオブジェクトのレイヤーは {first}", Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(4, 0, 4, 4),
            });
        }
    }

    /// <summary>「このレイヤーにそろえる」（そろえる先のレイヤー）。</summary>
    public event Action<int>? UnifyLayerRequested;

    /// <summary>主に選んでいる（最初に選んだ）オブジェクトのレイヤー。「そろえる先」の初めの値。</summary>
    public int? PrimaryLayer { get; set; }

    /// <summary>[This] の「描かれている範囲から計算…」。</summary>
    public event Action? ViewLimitRequested;
    /// <summary>[This] の「メインパネル」で表示灯を選んだ（-1 = なし）。</summary>
    public event Action<int>? MainPanelPicked;
    /// <summary>[This] の「解像度をメインパネルの幅にする」。</summary>
    public event Action? ResolutionFromMainRequested;
    /// <summary>[This] の「[This] の画像をメインパネルにする」。</summary>
    public event Action? ThisToMainRequested;

    /// <summary>メインパネルにできる表示灯（常表示のもの）。番号と名前。</summary>
    public List<(int Index, string Label)> MainPanelChoices { get; set; } = [];
    /// <summary>今のメインパネルの番号（なければ -1）。</summary>
    public int MainPanelIndex { get; set; } = -1;
    /// <summary>[This] に表示に使う画像がないか（画像なし、または前の版の透明な画像）。</summary>
    public bool ThisIsBlank { get; set; }
    /// <summary>デジタル数字の「1 コマの高さを画像から求める」。</summary>
    public event Action? IntervalRequested;
    /// <summary>中心と消失点の横の位置をそろえる（true: 消失点を中心に合わせる）。</summary>
    public event Action<bool>? AlignPointsRequested;

    private StackPanel? _pointsPanel;
    private bool _pointsBoth;

    /// <summary>パネル画面で中心と消失点を両方選んでいるか（そろえるボタンを出す）。</summary>
    public void SetPanelPoints(bool both)
    {
        _pointsBoth = both;
        if (_pointsPanel != null) _pointsPanel.Visibility = both ? Visibility.Visible : Visibility.Collapsed;
    }

    public string? BaseDir { get; set; }

    private bool _positionLocked = true;
    /// <summary>位置ロック中か（案内文の切り替えに使う）。</summary>
    public bool PositionLocked
    {
        get => _positionLocked;
        set
        {
            if (_positionLocked == value) return;
            _positionLocked = value;
            if (_hint != null) _hint.Text = HintText();
        }
    }

    private TextBlock? _hint;

    private string HintText() => _section?.Kind == SectionKind.This
        ? (PositionLocked
            ? "画面上の青い十字（中心）・橙の×（消失点）はクリックで選べます（選ぶと大きな輪が付きます。Shift＋クリックで両方選べます）。位置ロック中は動かせません。パネル画面左上の 🔒 で解除できます。点線の枠は表示範囲、一点鎖線は画面の目安（16:9・16:10・4:3 など。表示メニューの「画面枠の比率」で選べます）です。"
            : "画面上で青い十字（中心）・橙の×（消失点）をクリックで選び、ドラッグか矢印キー（1px、Shift で 10px）で動かせます。重なっているときはクリックするたびに入れ替わります。Shift＋クリックで両方選ぶと一緒に動かせ、横の位置をそろえるボタンが出ます。点線の枠は表示範囲、一点鎖線は画面の目安（16:9・16:10・4:3 など。表示メニューの「画面枠の比率」で選べます）です。")
        : (PositionLocked
            ? "位置ロック中は、画面上のドラッグや矢印キーで位置を動かせません（ここで数値を書き換えるのはできます）。パネル画面左上の 🔒 か Ctrl+L で解除できます。"
            : "画面上でドラッグで移動、矢印キーで 1px（Shift で 10px）。指針・ゲージは黄色い四角で半径を変えられます。");
    public bool V201 { get; set; }

    private static readonly string[] SubjectSuggestions = Subjects.Suggestions().ToArray();

    public PropertyEditor(StackPanel host, Window owner)
    {
        _host = host;
        _owner = owner;
    }

    /// <summary>セクションが変わったときだけ作り直し、同じなら値だけ更新する。</summary>
    public void Show(PanelSection? section, int index)
    {
        var sig = section == null ? "" : $"{index}|{section.HeaderRaw}|{string.Join(",", section.KeyValues.Select(k => k.Key.ToLowerInvariant()).Where(k => !IsSpecKey(section, k)))}|{V201}"
            + (section.Kind == SectionKind.This ? $"|{MainPanelIndex}|{ThisIsBlank}|{string.Join(",", MainPanelChoices.Select(c => c.Index + c.Label))}" : $"|{section.IsMainPanel}");
        if (sig == _builtSignature && section != null)
        {
            _section = section;
            UpdateValues();
            return;
        }
        _builtSignature = sig;
        _section = section;
        Build(index);
    }

    private static bool IsSpecKey(PanelSection s, string key) =>
        PanelSpec.For(s.Kind).Any(k => k.Name.Equals(key, StringComparison.OrdinalIgnoreCase));

    public void UpdateValues()
    {
        if (_section == null) return;
        foreach (var (key, set) in _setters) set(_section.Get(key) ?? "");
    }

    private void Build(int index)
    {
        _host.Children.Clear();
        _getters.Clear();
        _setters.Clear();
        _titleBox = null;
        _hint = null;
        _pointsPanel = null;
        if (_section == null)
        {
            _host.Children.Add(new TextBlock { Text = "オブジェクトを選ぶと、ここで値を編集できます。", Foreground = Brushes.Gray, Margin = new Thickness(4), TextWrapping = TextWrapping.Wrap });
            return;
        }
        var s = _section;
        _host.Children.Add(new TextBlock
        {
            Text = s.IsMainPanel ? $"{MainPanel.Title}（パネルの土台）  [{s.Name}]"
                : s.Kind == SectionKind.This && ThisIsBlank ? $"[This]（画像なし・パネル本体はメインパネル）"
                : $"{PanelSpec.KindLabel(s.Kind)}  [{s.Name}]",
            FontWeight = FontWeights.Bold, FontSize = 14, Margin = new Thickness(4, 4, 4, 2),
        });
        AddTitleRow();

        foreach (var spec in PanelSpec.For(s.Kind)) AddRow(spec);
        if (s.Kind == SectionKind.DigitalNumber)
        {
            var iv = new Button
            {
                Content = "1 コマの高さを画像から求める", Margin = new Thickness(4, 6, 4, 0), Padding = new Thickness(8, 2, 8, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = "数字の並んだ画像から、1 コマの高さ（Interval）を見つけて入れます。数字の間のすき間やくり返しから判断します（推定のときは知らせます）",
            };
            iv.Click += (_, _) => IntervalRequested?.Invoke();
            _host.Children.Add(iv);
        }
        if (s.Kind == SectionKind.This)
        {
            // メインパネル（パネルの土台にする表示灯）
            var mainRow = new DockPanel { Margin = new Thickness(4, 8, 4, 0) };
            mainRow.Children.Add(new TextBlock { Text = "メインパネル", Width = 110, VerticalAlignment = VerticalAlignment.Center, ToolTip = "パネルの土台にする、常表示の表示灯。解像度・表示範囲はこの画像から決めます" });
            var combo = new ComboBox { ToolTip = "パネルの土台にする表示灯（常表示のもの）を選びます。[This] の画像は透明にして、表示には使いません" };
            combo.Items.Add("（なし）");
            foreach (var c in MainPanelChoices) combo.Items.Add(c.Label);
            combo.SelectedIndex = MainPanelChoices.FindIndex(c => c.Index == MainPanelIndex) + 1;
            combo.SelectionChanged += (_, _) =>
            {
                int k = combo.SelectedIndex - 1;
                int pick = k >= 0 && k < MainPanelChoices.Count ? MainPanelChoices[k].Index : -1;
                if (pick != MainPanelIndex) MainPanelPicked?.Invoke(pick);
            };
            mainRow.Children.Add(combo);
            _host.Children.Add(mainRow);
            var mainButtons = new WrapPanel { Margin = new Thickness(4, 4, 4, 0) };
            if (MainPanelIndex < 0 && !ThisIsBlank && !string.IsNullOrWhiteSpace(s.Get("DaytimeImage")))
            {
                var toMain = new Button { Content = "[This] の画像をメインパネルにする", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 4),
                    ToolTip = "[This] の画像を、常表示の表示灯（メインパネル・レイヤー 1）に移し、[This] は透明な画像にします。ほかのオブジェクトはメインパネルより上になるようにレイヤーを上げます" };
                toMain.Click += (_, _) => ThisToMainRequested?.Invoke();
                mainButtons.Children.Add(toMain);
            }
            var res = new Button { Content = "解像度をメインパネルの幅にする", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 4), IsEnabled = MainPanelIndex >= 0,
                ToolTip = "解像度（Resolution）を、メインパネルの画像の幅にします（ズーム 1 倍でメインパネルの横幅がちょうど画面に入る）" };
            res.Click += (_, _) => ResolutionFromMainRequested?.Invoke();
            mainButtons.Children.Add(res);
            _host.Children.Add(mainButtons);

            var calc = new Button
            {
                Content = "表示範囲を描かれている範囲から計算…", Margin = new Thickness(4, 6, 4, 0), Padding = new Thickness(8, 2, 8, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = "メインパネル（なければ [This]）の画像で、透明な所やまわりを埋めている一定の色を除いた範囲を見つけ、そこから余白を引いて Left・Top・Right・Bottom に入れます",
            };
            calc.Click += (_, _) => ViewLimitRequested?.Invoke();
            _host.Children.Add(calc);

            // 中心と消失点を両方選んだときだけ出す
            _pointsPanel = new StackPanel { Margin = new Thickness(4, 8, 4, 0), Visibility = _pointsBoth ? Visibility.Visible : Visibility.Collapsed };
            _pointsPanel.Children.Add(new TextBlock { Text = "中心と消失点の横の位置をそろえる", Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 2) });
            var row = new WrapPanel();
            var toCenter = new Button { Content = "消失点を中心に合わせる", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 4), ToolTip = "消失点（Origin）の X を、中心（Center）の X と同じにします。縦の位置は変えません" };
            toCenter.Click += (_, _) => AlignPointsRequested?.Invoke(true);
            var toOrigin = new Button { Content = "中心を消失点に合わせる", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 4), ToolTip = "中心（Center）の X を、消失点（Origin）の X と同じにします。縦の位置は変えません" };
            toOrigin.Click += (_, _) => AlignPointsRequested?.Invoke(false);
            row.Children.Add(toCenter);
            row.Children.Add(toOrigin);
            _pointsPanel.Children.Add(row);
            _host.Children.Add(_pointsPanel);
        }

        var extra = s.KeyValues.Where(k => !IsSpecKey(s, k.Key)).Select(k => k.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (extra.Count > 0)
        {
            _host.Children.Add(new TextBlock { Text = "使えないキー（BVE は無視します）", Foreground = Brushes.Firebrick, Margin = new Thickness(4, 10, 4, 2) });
            foreach (var k in extra) AddRow(new KeySpec(k, k, KeyType.Text, "このセクションでは使えないキーです。空にすると行を消します。"));
        }
        _hint = new TextBlock { Text = HintText(), Foreground = Brushes.Gray, Margin = new Thickness(4, 12, 4, 4), TextWrapping = TextWrapping.Wrap };
        _host.Children.Add(_hint);
    }

    private TextBox? _titleBox;
    private TextBox? _groupLocationBox;

    /// <summary>グループごと選んでいるとき。</summary>
    public void ShowGroup(string name, int count, Point? topLeft, IReadOnlyList<PanelSection> sections)
    {
        var sig = $"group|{name}|{count}|{PrimaryLayer}";
        string loc = topLeft is { } p ? PanelValues.Point(p.X, p.Y) : "";
        _multiSections = sections;
        if (sig == _builtSignature && _groupLocationBox != null)
        {
            if (!_groupLocationBox.IsKeyboardFocusWithin) _groupLocationBox.Text = loc;
            UpdateMultiValues();
            return;
        }
        _builtSignature = sig;
        _section = null;
        _host.Children.Clear();
        _getters.Clear();
        _setters.Clear();
        _titleBox = null;
        _hint = null;
        _pointsPanel = null;

        _host.Children.Add(new TextBlock { Text = $"グループ（オブジェクト {count} 個）", FontWeight = FontWeights.Bold, FontSize = 14, Margin = new Thickness(4, 4, 4, 6) });

        var nameBox = AddSimpleRow("グループ名", name, "パネルファイルでは開始行「;===== グループ: 名前 =====」と終了行に書きます", t => { if (t.Length > 0 && t != name) GroupRenameCommit?.Invoke(t); });
        nameBox.FontWeight = FontWeights.SemiBold;
        _groupLocationBox = AddSimpleRow("位置（左上）", loc, "グループのオブジェクトの Location のいちばん左上。書き換えると全部を同じだけ動かします",
            t => { if (PanelValues.TryPoint(t, out var x, out var y)) GroupLocationCommit?.Invoke(x, y); });

        var buttons = new WrapPanel { Margin = new Thickness(4, 10, 4, 4) };
        buttons.Children.Add(MakeButton("グループを解除", () => UngroupRequested?.Invoke()));
        buttons.Children.Add(MakeButton("グループを複製", () => DuplicateGroupRequested?.Invoke()));
        _host.Children.Add(buttons);
        AddLayerButtons(sections);
        AddAlignButtons("中のオブジェクトを整列");
        AddMultiRows(sections);
        _host.Children.Add(new TextBlock
        {
            Text = "パネル画面でクリックするとグループ全体を選び、ドラッグ・矢印キーでまとめて動かせます（位置ロックの解除が必要）。中の 1 つだけを動かすときはダブルクリックするか、一覧でそのオブジェクトを選んでください。",
            Foreground = Brushes.Gray, Margin = new Thickness(4, 8, 4, 4), TextWrapping = TextWrapping.Wrap,
        });
    }

    /// <summary>グループでない複数のオブジェクトを選んでいるとき。</summary>
    public void ShowMulti(int count, int groupable, IReadOnlyList<PanelSection> sections)
    {
        var sig = $"multi|{count}|{groupable}|{PrimaryLayer}|{string.Join(",", sections.Select(x => x.GetHashCode()))}";
        _multiSections = sections;
        if (sig == _builtSignature) { UpdateMultiValues(); return; }
        _builtSignature = sig;
        _section = null;
        _host.Children.Clear();
        _getters.Clear();
        _setters.Clear();
        _titleBox = null;
        _hint = null;
        _pointsPanel = null;
        _groupLocationBox = null;
        _host.Children.Add(new TextBlock { Text = $"{count} 個選択中", FontWeight = FontWeights.Bold, FontSize = 14, Margin = new Thickness(4, 4, 4, 6) });
        var buttons = new WrapPanel { Margin = new Thickness(4, 4, 4, 4) };
        var make = MakeButton("グループ化する（Ctrl+G）", () => MakeGroupRequested?.Invoke());
        make.IsEnabled = groupable > 0;
        buttons.Children.Add(make);
        _host.Children.Add(buttons);
        AddLayerButtons(sections);
        AddAlignButtons("整列");
        AddMultiRows(sections);
        _host.Children.Add(new TextBlock
        {
            Text = "ドラッグ・矢印キーで選んだオブジェクトをまとめて動かせます（位置ロックの解除が必要）。一覧では Ctrl／Shift＋クリック、パネル画面では Shift＋クリックか、何もない所からドラッグして囲むと選べます（Ctrl+A ですべて）。",
            Foreground = Brushes.Gray, Margin = new Thickness(4, 8, 4, 4), TextWrapping = TextWrapping.Wrap,
        });
    }

    // ================= 複数のオブジェクトの値・整列 =================

    private IReadOnlyList<PanelSection> _multiSections = [];
    private readonly List<(string Key, TextBox Box, TextBlock Hint)> _multiRows = [];

    /// <summary>位置と画像はオブジェクトごとに違うので、まとめて編集する欄には出さない。</summary>
    private static readonly HashSet<string> NotShared = new(StringComparer.OrdinalIgnoreCase)
        { "Location", "Origin", "Center", "DaytimeImage", "NighttimeImage" };

    private static readonly (string Label, AlignKind Kind)[] AlignButtons =
    [
        ("左", AlignKind.Left), ("左右中央", AlignKind.CenterX), ("右", AlignKind.Right),
        ("上", AlignKind.Top), ("上下中央", AlignKind.CenterY), ("下", AlignKind.Bottom),
        ("横に等間隔", AlignKind.DistributeX), ("縦に等間隔", AlignKind.DistributeY),
    ];

    private void AddAlignButtons(string title)
    {
        _host.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 8, 4, 2) });
        var wrap = new WrapPanel { Margin = new Thickness(4, 0, 4, 4) };
        foreach (var (label, kind) in AlignButtons)
        {
            var b = MakeButton(label, () => AlignRequested?.Invoke(kind));
            b.Padding = new Thickness(6, 2, 6, 2);
            b.ToolTip = $"{label}{(kind is AlignKind.DistributeX or AlignKind.DistributeY ? "（3 つ以上）" : "揃え")}。見た目の枠で揃えます";
            wrap.Children.Add(b);
        }
        _host.Children.Add(wrap);
    }

    /// <summary>選んだオブジェクトすべてに共通するキーの欄（値がそろっていなければ空欄で「ばらばら」）。</summary>
    private void AddMultiRows(IReadOnlyList<PanelSection> sections)
    {
        _multiRows.Clear();
        var kinds = sections.Select(x => x.Kind).Distinct().ToList();
        if (kinds.Count == 0 || kinds.Contains(SectionKind.Unknown)) return;
        var specs = PanelSpec.For(kinds[0]).Where(k => !NotShared.Contains(k.Name)
            && kinds.All(kd => PanelSpec.For(kd).Any(o => o.Name == k.Name))).ToList();
        if (specs.Count == 0) return;
        _host.Children.Add(new TextBlock
        {
            Text = $"まとめて編集（{string.Join("・", kinds.Select(PanelSpec.KindLabel))} {sections.Count} 個）",
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 10, 4, 2),
        });
        foreach (var spec in specs)
        {
            var grid = new Grid { Margin = new Thickness(4, 2, 4, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            const bool off = false;
            var tip = $"{spec.Name}\n{spec.Help}\n選んだオブジェクトすべてに同じ値を書きます。空にして確定すると、すべてのオブジェクトからこのキーを消します";
            grid.Children.Add(new TextBlock { Text = spec.Label, VerticalAlignment = VerticalAlignment.Center, ToolTip = tip, Foreground = off ? Brushes.Gray : Brushes.Black });
            var box = new TextBox { VerticalContentAlignment = VerticalAlignment.Center, ToolTip = tip };
            var hint = new TextBlock { Text = "（ばらばら）", Foreground = Brushes.Gray, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            Grid.SetColumn(box, 1);
            Grid.SetColumn(hint, 1);
            grid.Children.Add(box);
            grid.Children.Add(hint);
            var key = spec.Name;
            void Apply()
            {
                var (common, mixed) = CommonValue(key);
                var t = box.Text.Trim();
                if (mixed && t.Length == 0) return;          // ばらばらのまま触っていない
                if (!mixed && t == common) return;
                MultiCommit?.Invoke(key, t);
            }
            box.LostKeyboardFocus += (_, _) => Apply();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Apply(); box.SelectAll(); } };
            box.TextChanged += (_, _) => hint.Visibility = box.Text.Length == 0 && CommonValue(key).Mixed ? Visibility.Visible : Visibility.Collapsed;
            _multiRows.Add((key, box, hint));
            _host.Children.Add(grid);
        }
        UpdateMultiValues();
    }

    private (string Common, bool Mixed) CommonValue(string key)
    {
        var values = _multiSections.Select(x => x.Get(key) ?? "").Distinct().ToList();
        return values.Count == 1 ? (values[0], false) : ("", true);
    }

    private void UpdateMultiValues()
    {
        foreach (var (key, box, hint) in _multiRows)
        {
            var (common, mixed) = CommonValue(key);
            if (!box.IsKeyboardFocusWithin) box.Text = common;
            hint.Visibility = mixed && box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static Button MakeButton(string text, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 4) };
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>名前と入力欄だけの行（Enter かフォーカスが外れたら確定）。</summary>
    private TextBox AddSimpleRow(string label, string value, string tip, Action<string> commit)
    {
        var grid = new Grid { Margin = new Thickness(4, 2, 4, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var box = new TextBox { Text = value, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = tip };
        string last = value;
        void Apply()
        {
            var t = box.Text.Trim();
            if (t == last) return;
            last = t;
            commit(t);
        }
        box.LostKeyboardFocus += (_, _) => Apply();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Apply(); box.SelectAll(); } };
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, ToolTip = tip });
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);
        _host.Children.Add(grid);
        return box;
    }

    /// <summary>名前の入力欄にカーソルを移す（一覧の F2 などから）。</summary>
    public void FocusTitle()
    {
        if (_titleBox == null) return;
        _titleBox.Focus();
        _titleBox.SelectAll();
    }

    private void AddTitleRow()
    {
        var s = _section!;
        var grid = new Grid { Margin = new Thickness(4, 2, 4, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var tip = "オブジェクトの名前。パネルファイルでは見出し [ ] のすぐ上のコメント行（;名前）として保存します。\n"
                + "見出し行の後ろに書いた名前（[PilotLamp]##名前）はその場所のまま書き換えます。空にすると名前の行を消します。";
        var label = new TextBlock { Text = "名前", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
        var box = new TextBox { Text = s.Title, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = tip, FontWeight = FontWeights.SemiBold };
        void Apply()
        {
            var t = box.Text.Trim();
            if (_section != null && t != _section.Title) TitleCommit?.Invoke(t);
        }
        box.LostKeyboardFocus += (_, _) => Apply();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Apply(); box.SelectAll(); }
            else if (e.Key == Key.Escape) box.Text = _section?.Title ?? "";
        };
        Grid.SetColumn(box, 1);
        grid.Children.Add(label);
        grid.Children.Add(box);
        _host.Children.Add(grid);
        _titleBox = box;
        _setters["\0title"] = _ => { if (!box.IsKeyboardFocusWithin) box.Text = _section?.Title ?? ""; };
    }

    private void AddRow(KeySpec spec)
    {
        var s = _section!;
        var grid = new Grid { Margin = new Thickness(4, 2, 4, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        const bool disabled = false;
        var label = new TextBlock
        {
            Text = spec.Label,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = $"{spec.Name}\n{spec.Help}" + (spec.V201Only ? "\n※ BVE 5.8 以降・BVE6 で使えるキー（書式 2.00 のパネルでも使えます。BVE 5.7 以前では無視されます）" : ""),
            Foreground = disabled ? Brushes.Gray : Brushes.Black,
        };
        grid.Children.Add(label);

        string current = s.Get(spec.Name) ?? "";
        FrameworkElement editor;

        if (spec.Type == KeyType.Subject)
        {
            var combo = new ComboBox { IsEditable = true, ItemsSource = SubjectSuggestions, Text = current, IsTextSearchEnabled = true };
            var desc = new TextBlock { Foreground = Brushes.DimGray, FontSize = 11, Text = Subjects.Describe(current) };
            void Apply()
            {
                var t = combo.Text.Trim();
                desc.Text = Subjects.Describe(t);
                if (t != (_section?.Get(spec.Name) ?? "")) Commit?.Invoke(spec.Name, t);
            }
            combo.LostKeyboardFocus += (_, _) => Apply();
            combo.DropDownClosed += (_, _) => Apply();
            combo.KeyDown += (_, e) => { if (e.Key == Key.Enter) Apply(); };
            var stack = new StackPanel();
            stack.Children.Add(combo);
            stack.Children.Add(desc);
            editor = stack;
            _setters[spec.Name] = v => { if (!combo.IsKeyboardFocusWithin) { combo.Text = v; desc.Text = Subjects.Describe(v); } };
        }
        else if (spec.Type == KeyType.Bool)
        {
            var combo = new ComboBox { ItemsSource = new[] { "", "True", "False" } };
            combo.SelectedItem = PanelValues.TryBool(current, out var b) ? (b ? "True" : "False") : "";
            combo.SelectionChanged += (_, _) =>
            {
                var t = combo.SelectedItem as string ?? "";
                if (!string.Equals(t, _section?.Get(spec.Name) ?? "", StringComparison.OrdinalIgnoreCase)) Commit?.Invoke(spec.Name, t);
            };
            editor = combo;
            _setters[spec.Name] = v => combo.SelectedItem = PanelValues.TryBool(v, out var bb) ? (bb ? "True" : "False") : "";
        }
        else
        {
            // テスト用の裏機能（Ctrl+9）でファイル名だけを出しているときは、画像の欄を書き換えられないようにする（短くした名前で上書きしないため）
            bool namesOnly = spec.Type == KeyType.Image && PathDisplay.NamesOnly;
            var box = new TextBox { Text = namesOnly ? PathDisplay.Show(current) : current, VerticalContentAlignment = VerticalAlignment.Center, IsReadOnly = namesOnly };
            void Apply()
            {
                if (namesOnly) return;
                var t = box.Text.Trim();
                if (t != (_section?.Get(spec.Name) ?? "")) Commit?.Invoke(spec.Name, t);
                Validate(box, spec);
            }
            box.LostKeyboardFocus += (_, _) => Apply();
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { Apply(); box.SelectAll(); }
                else if (e.Key == Key.Escape) box.Text = _section?.Get(spec.Name) ?? "";
            };
            Validate(box, spec);
            editor = box;
            _setters[spec.Name] = v => { if (!box.IsKeyboardFocusWithin) { box.Text = namesOnly ? PathDisplay.Show(v) : v; Validate(box, spec); } };

            if (spec.Type == KeyType.Image)
            {
                var btn = new Button { Content = "…", Width = 24, Margin = new Thickness(2, 0, 0, 0), ToolTip = "画像を選ぶ", IsEnabled = !namesOnly };
                btn.Click += (_, _) =>
                {
                    var dlg = new OpenFileDialog { AddToRecent = false, Filter = "画像|*.png;*.bmp;*.jpg;*.jpeg;*.gif|すべて|*.*" };
                    if (BaseDir != null && Directory.Exists(BaseDir)) dlg.InitialDirectory = BaseDir;
                    if (dlg.ShowDialog(_owner) != true) return;
                    var rel = BaseDir == null ? dlg.FileName : RelativePath(BaseDir, dlg.FileName);
                    box.Text = rel;
                    Commit?.Invoke(spec.Name, rel);
                };
                Grid.SetColumn(btn, 2);
                grid.Children.Add(btn);
            }
            else if (spec.Type == KeyType.Color)
            {
                var swatch = new Border { Width = 22, Height = 20, Margin = new Thickness(2, 0, 0, 0), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Cursor = Cursors.Hand, ToolTip = "色を選ぶ" };
                void Paint(string v) => swatch.Background = PanelValues.TryColor(v, out var r, out var g, out var b2) ? new SolidColorBrush(Color.FromRgb(r, g, b2)) : Brushes.Transparent;
                Paint(current);
                box.TextChanged += (_, _) => Paint(box.Text);
                swatch.MouseLeftButtonUp += (_, _) =>
                {
                    var init = PanelValues.TryColor(box.Text, out var r, out var g, out var b2) ? Color.FromRgb(r, g, b2) : Colors.White;
                    if (ColorPicker.Pick(_owner, init) is { } c)
                    {
                        var hex = PanelValues.Color(c.R, c.G, c.B);
                        box.Text = hex;
                        Commit?.Invoke(spec.Name, hex);
                    }
                };
                Grid.SetColumn(swatch, 2);
                grid.Children.Add(swatch);
            }
        }

        editor.IsEnabled = true;
        editor.ToolTip = label.ToolTip;
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        _host.Children.Add(grid);
    }

    private static void Validate(TextBox box, KeySpec spec)
    {
        var t = box.Text.Trim();
        bool ok = t.Length == 0 || spec.Type switch
        {
            KeyType.Int or KeyType.Number or KeyType.Angle => PanelValues.TryDouble(t, out _),
            KeyType.Point => PanelValues.TryPoint(t, out _, out _),
            KeyType.Color => PanelValues.TryColor(t, out _, out _, out _),
            _ => true,
        };
        box.BorderBrush = ok ? SystemColors.ControlDarkBrush : Brushes.Red;
        box.Background = ok ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xFF, 0xEE, 0xEE));
    }

    public static string RelativePath(string baseDir, string fullPath)
    {
        var rel = Path.GetRelativePath(baseDir, fullPath);
        return Path.IsPathRooted(rel) ? fullPath : rel;
    }
}
