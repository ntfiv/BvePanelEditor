using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BvePanelEditor.Core;
using BvePanelEditor.Views;
using Microsoft.Win32;

namespace BvePanelEditor;

/// <summary>
/// バリエーション展開: マスターパネル（全部入り）のオブジェクトごとに入るバリエーションを決め、バリエーションごとのパネルファイルを書き出す。
/// ファイルの印は「; @group 条件」〜「; @end」、一覧は前置きの「;@バリエーション: 名前 = 出力ファイル」。
/// </summary>
public partial class MainWindow
{
    /// <summary>表示中のバリエーション（null ならマスター＝すべて）。</summary>
    private string? _viewVariation;
    private bool _suppressVariationUi;

    private readonly ListBox _varList = new() { Height = 150, Margin = new Thickness(0, 4, 0, 4), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _varName = new() { Margin = new Thickness(0, 2, 0, 2) };
    private readonly TextBox _varFile = new() { Margin = new Thickness(0, 2, 0, 2) };
    private readonly WrapPanel _varChecks = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _varCheckTitle = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
    private readonly StackPanel _varEdit = new();

    /// <summary>右の欄に「バリエーション」のタブを足す。</summary>
    private void BuildVariationTab()
    {
        var root = new StackPanel { Margin = new Thickness(8) };
        root.Children.Add(new TextBlock
        {
            Text = "全部入りのパネル（マスターパネル）から、バリエーション（路線など）ごとのパネルファイルを書き出します。"
                + "オブジェクトごとに入るバリエーションを決めます（パネル画面の左上で表示するバリエーションを選ぶと、一覧の目のマークで入れる・外すを切り替えられます）。"
                + "パネルファイルにはコメント「; @group 名前」〜「; @end」で書きます。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 6),
        });
        root.Children.Add(new TextBlock { Text = "バリエーション（名前 → 出力ファイル）", FontWeight = FontWeights.SemiBold });
        _varList.SelectionChanged += (_, _) => { if (!_suppressVariationUi) ShowVariationEdit(); };
        // ダブルクリックで表示する（いちばん上のマスターの行ならマスター）
        _varList.MouseDoubleClick += (_, _) => { if (_varList.SelectedIndex == 0) SetViewVariation(null); else if (SelectedVariation is { } v) SetViewVariation(v.Name); };
        root.Children.Add(_varList);

        root.Children.Add(VarButton("現在の表示状態をバリエーションとして追加", OnAddVariation,
            "今パネル画面に出ているオブジェクト（目のマークで隠していないもの。バリエーションを表示中なら、そのバリエーションのオブジェクト）が入るバリエーションを足します"));
        var row2 = new WrapPanel();
        row2.Children.Add(_varDuplicate = VarButton("バリエーションを複製", OnDuplicateVariation, "選んだバリエーションと同じオブジェクトが入るバリエーションを足します"));
        row2.Children.Add(_varDelete = VarButton("バリエーションを削除", OnDeleteVariation, "選んだバリエーションを一覧から消します"));
        root.Children.Add(row2);
        var row3 = new WrapPanel();
        row3.Children.Add(_varUp = VarButton("↑", () => MoveVariation(-1), "一覧で上へ"));
        row3.Children.Add(_varDown = VarButton("↓", () => MoveVariation(1), "一覧で下へ"));
        row3.Children.Add(VarButton("表示する", () => { if (_varList.SelectedIndex == 0) SetViewVariation(null); else if (SelectedVariation is { } v) SetViewVariation(v.Name); }, "パネル画面をこのバリエーションの見た目にします（一覧の目のマークで入れる・外す。ダブルクリックでも）"));
        row3.Children.Add(VarButton("マスターを表示", () => SetViewVariation(null), "すべてのオブジェクトを表示します"));
        root.Children.Add(row3);

        _varEdit.Children.Add(new TextBlock { Text = "名前（空白と , ! ; # [ ] = は使えません）", Margin = new Thickness(0, 6, 0, 0) });
        _varName.LostKeyboardFocus += (_, _) => CommitVariationName();
        _varName.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) CommitVariationName(); };
        _varEdit.Children.Add(_varName);
        _varEdit.Children.Add(new TextBlock { Text = "出力ファイル（マスターのフォルダからの場所）" });
        var fileRow = new DockPanel();
        var pick = VarButton("選ぶ…", OnPickVariationFile, "書き出すパネルファイルを選びます");
        DockPanel.SetDock(pick, Dock.Right);
        fileRow.Children.Add(pick);
        _varFile.LostKeyboardFocus += (_, _) => CommitVariationFile();
        _varFile.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) CommitVariationFile(); };
        fileRow.Children.Add(_varFile);
        _varEdit.Children.Add(fileRow);
        root.Children.Add(_varEdit);

        var export = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        export.Children.Add(_varExportOne = VarButton("選んだものを書き出す", () => { if (SelectedVariation is { } v) ExportVariations([v]); }, "選んだバリエーションのパネルファイルを書き出します"));
        export.Children.Add(VarButton("すべて書き出す", () => { if (_doc != null) ExportVariations(_doc.Variations.ToList()); }, "すべてのバリエーションのパネルファイルを書き出します"));
        root.Children.Add(export);

        root.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 6) });
        root.Children.Add(new TextBlock { Text = "選んでいるオブジェクトが入るバリエーション", FontWeight = FontWeights.SemiBold });
        root.Children.Add(_varCheckTitle);
        root.Children.Add(_varChecks);

        root.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 6) });
        root.Children.Add(VarButton("既存のパネルからマスターを作る…", OnImportVariations,
            "バリエーションごとに作ってあるパネルファイルを集めて、同じオブジェクトを 1 つにまとめたマスターパネルを作ります"));

        _variationTab = new TabItem { Header = "バリエーション", Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        RightTabs.Items.Add(_variationTab);
        RightTabs.SelectionChanged += OnRightTabChanged;
    }

    private static Button VarButton(string text, Action click, string tip)
    {
        var b = new Button { Content = text, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 2, 4, 2), ToolTip = tip };
        b.Click += (_, _) => click();
        return b;
    }

    private Button? _varDuplicate, _varDelete, _varUp, _varDown, _varExportOne;

    /// <summary>一覧で選んでいるバリエーション（一覧のいちばん上はマスターの行なので、そこなら null）。</summary>
    private PanelVariation? SelectedVariation => _doc != null && _varList.SelectedIndex >= 1 && _varList.SelectedIndex <= _doc.Variations.Count ? _doc.Variations[_varList.SelectedIndex - 1] : null;

    /// <summary>一覧で i 番目のバリエーションを選ぶ（いちばん上のマスターの行の次が 0 番）。</summary>
    private void SelectVariationRow(int i) => _varList.SelectedIndex = i + 1;

    /// <summary>バリエーションの一覧・表示の切り替え・チェックを今のパネルに合わせる（RebuildAll・選択が変わったとき）。</summary>
    private void RefreshVariationUi()
    {
        _suppressVariationUi = true;
        var vars = _doc?.Variations ?? [];
        int keep = _varList.SelectedIndex;
        // いちばん上はマスターパネル（名前・出力ファイルは変えられない）。表示中のものには左に「▶」を付ける
        var rows = new List<string> { $"{(_viewVariation == null ? "▶ " : "　 ")}マスター（すべて）　→　{(_path != null ? Path.GetFileName(_path) : "（未保存）")}" };
        rows.AddRange(vars.Select(v => $"{(v.Name == _viewVariation ? "▶ " : "　 ")}{v.Name}　→　{(v.File.Length > 0 ? v.File : "（出力ファイルなし）")}"));
        _varList.ItemsSource = rows;
        _varList.SelectedIndex = Math.Clamp(keep < 0 ? (vars.Count > 0 ? 1 : 0) : keep, 0, vars.Count);
        // 表示の切り替え
        var items = new List<string> { "表示: マスター（すべて）" };
        items.AddRange(vars.Select(v => $"表示: {v.Name}"));
        VariationViewBox.ItemsSource = items;
        VariationViewBox.SelectedIndex = _viewVariation == null ? 0 : vars.FindIndex(v => v.Name == _viewVariation) + 1;
        VariationViewBox.Visibility = vars.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateVariationModeBadge();
        _suppressVariationUi = false;
        ShowVariationEdit();
        RefreshVariationChecks();
    }

    private void ShowVariationEdit()
    {
        var v = SelectedVariation;
        // マスターの行では、名前・出力ファイル・複製・削除・並べ替え・選んだものの書き出しは使えない
        _varEdit.IsEnabled = v != null;
        foreach (var b in new[] { _varDelete, _varUp, _varDown, _varExportOne }) if (b != null) b.IsEnabled = v != null;
        if (_varDuplicate != null) _varDuplicate.IsEnabled = v != null || _varList.SelectedIndex == 0; // マスターの行も複製できる（すべてのオブジェクトが入る）
        _varName.Text = v?.Name ?? "";
        _varFile.Text = v?.File ?? "";
    }

    /// <summary>選んでいるオブジェクトが入るバリエーションのチェック。</summary>
    private void RefreshVariationChecks()
    {
        _varChecks.Children.Clear();
        if (_doc == null || _doc.Variations.Count == 0) { _varCheckTitle.Text = "バリエーションを足すと、ここで選べます"; return; }
        var targets = VariationTargets();
        if (targets.Count == 0) { _varCheckTitle.Text = "オブジェクトを選んでください"; return; }
        bool common = targets.All(i => _doc.Sections[i].VariationCond == null);
        _varCheckTitle.Text = (targets.Count == 1 ? "" : $"{targets.Count} 個　") + (common ? "共通（すべてのバリエーションに入ります）" : "チェックしたバリエーションに入ります");
        foreach (var v in _doc.Variations)
        {
            int n = targets.Count(i => _doc.Sections[i].InVariation(v.Name));
            var cb = new CheckBox { Content = v.Name, Margin = new Thickness(0, 2, 10, 2), IsThreeState = false, IsChecked = n == targets.Count ? true : n == 0 ? false : null };
            var name = v.Name;
            cb.Click += (_, _) => SetMembership(VariationTargets(), name, cb.IsChecked == true);
            _varChecks.Children.Add(cb);
        }
    }

    /// <summary>入る・外すを切り替えるオブジェクト（グループごと選んでいればその中身。[This] も入れられる）。</summary>
    private List<int> VariationTargets()
    {
        if (_doc == null) return [];
        if (_selGroup != null) return _doc.Members(_selGroup);
        return _selSet.Count > 0 ? [.. _selSet] : _selected >= 0 ? [_selected] : [];
    }

    private void SetMembership(IReadOnlyList<int> targets, string name, bool include)
    {
        if (_doc == null || targets.Count == 0) return;
        FlushReparse();
        _doc.SetMembership(targets, name, include);
        CommitModel();
        Message($"{targets.Count} 個をバリエーション「{name}」に{(include ? "入れました" : "入れないようにしました")}");
    }

    /// <summary>目のマーク: 全部入っていれば外し、そうでなければ入れる。</summary>
    private void ToggleMembership(IReadOnlyList<int> targets, string name)
    {
        if (_doc == null || targets.Count == 0) return;
        SetMembership(targets, name, !targets.All(i => _doc.Sections[i].InVariation(name)));
    }

    /// <summary>
    /// グループの見出しに出すバリエーション: 中のオブジェクトが全部入っているバリエーション（「・」でつなぐ）。
    /// 全部のバリエーションに入っている（共通）・どれにも全部は入っていないときは空。一覧がなければ、中のオブジェクトの条件がそろっているときだけその条件。
    /// </summary>
    private string GroupVariationText(IReadOnlyList<int> members)
    {
        if (_doc == null || members.Count == 0) return "";
        if (_doc.Variations.Count == 0)
        {
            var c = _doc.Sections[members[0]].VariationCond;
            return c != null && members.All(m => _doc.Sections[m].VariationCond == c) ? c.Replace(", ", "・") : "";
        }
        var all = _doc.VariationNames;
        var inAll = all.Where(v => members.All(m => _doc.Sections[m].InVariation(v))).ToList();
        return inAll.Count == 0 || inAll.Count == all.Count ? "" : string.Join("・", inAll);
    }

    /// <summary>バリエーションを表示しているとき、新しく足すオブジェクトはそのバリエーションだけに入れる。</summary>
    private void JoinViewVariation(PanelSection s)
    {
        if (_viewVariation != null && _doc != null && _doc.VariationNames.Contains(_viewVariation)) s.VariationCond = _viewVariation;
    }

    private string ViewVariationNote => _viewVariation != null ? $"（バリエーション「{_viewVariation}」だけに入れました）" : "";

    // ---- バリエーション編集モード（バリエーションのタブを開いている間） ----

    private bool _variationMode;
    private bool _lockBeforeVariationMode = true;
    private TabItem? _variationTab;

    /// <summary>
    /// バリエーションのタブを開いている間は「バリエーション編集モード」: オブジェクトの位置は動かさない（位置ロックに固定）。
    /// パネル画面の左上の位置ロック・表示灯のボタンは隠し、「バリエーション編集モード」と出す。
    /// </summary>
    private void OnRightTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != RightTabs) return;
        bool on = _variationTab != null && RightTabs.SelectedItem == _variationTab;
        if (on == _variationMode) return;
        _variationMode = on;
        if (on)
        {
            _lockBeforeVariationMode = Canvas.PositionLocked;
            Canvas.PositionLocked = true;
            Canvas.ForceLocked = true;
            LockMenu.IsChecked = true;
            _props.PositionLocked = true;
        }
        else
        {
            Canvas.ForceLocked = false;
            Canvas.PositionLocked = _lockBeforeVariationMode;
            LockButton.IsChecked = LockMenu.IsChecked = _lockBeforeVariationMode;
            _props.PositionLocked = _lockBeforeVariationMode;
        }
        LockButton.Visibility = AllLampsCheck.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        VariationModeBadge.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        UpdateCoverButton();
        UpdateVariationModeBadge();
        Message(on ? "バリエーション編集モードです（オブジェクトの位置は動かしません）。ほかのタブに戻ると、運転台の編集に戻ります" : "運転台の編集に戻りました");
    }

    private static readonly System.Windows.Media.Brush BadgeMaster = Frozen(0xE8, 0xC0, 0x30, 0x30), BadgeMasterBorder = Frozen(0xFF, 0xFF, 0xC0, 0xC0);
    private static readonly System.Windows.Media.Brush BadgeVariation = Frozen(0xE8, 0x1B, 0x5F, 0xC1), BadgeVariationBorder = Frozen(0xFF, 0xB8, 0xD4, 0xFF);
    private static System.Windows.Media.Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var br = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(a, r, g, b));
        br.Freeze();
        return br;
    }

    /// <summary>「バリエーション編集モード」の表示。マスターを表示しているときは赤、バリエーションを表示しているときは青。</summary>
    private void UpdateVariationModeBadge()
    {
        VariationModeText.Text = "バリエーション編集モード" + (_viewVariation != null ? $"（表示: {_viewVariation}）" : "（表示: マスター）");
        VariationModeBadge.Background = _viewVariation == null ? BadgeMaster : BadgeVariation;
        VariationModeBadge.BorderBrush = _viewVariation == null ? BadgeMasterBorder : BadgeVariationBorder;
    }

    /// <summary>バリエーション編集モードでは位置ロックを外せない。外そうとしたら知らせて true。</summary>
    private bool BlockUnlockInVariationMode(bool locked)
    {
        if (_variationMode && locked) _lockBeforeVariationMode = true; // モード中にロックした（ファイルを開いたときなど）なら、抜けたあともロック
        if (!_variationMode || locked) return false;
        LockButton.IsChecked = LockMenu.IsChecked = true;
        Message("バリエーション編集モードではオブジェクトの位置は動かせません。ほかのタブ（プロパティなど）に戻ると、位置ロックを外せます");
        return true;
    }

    // ---- 表示の切り替え ----

    private void OnVariationViewBox(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressVariationUi || _doc == null) return;
        int i = VariationViewBox.SelectedIndex;
        SetViewVariation(i <= 0 || i > _doc.Variations.Count ? null : _doc.Variations[i - 1].Name);
    }

    /// <summary>表示するバリエーションを変える（null ならマスター）。</summary>
    public void SetViewVariation(string? name)
    {
        if (_doc == null) return;
        _viewVariation = name != null && _doc.VariationNames.Contains(name) ? name : null;
        RebuildAll();
        UpdateVariationModeBadge();
        Message(_viewVariation == null ? "マスター（すべてのオブジェクト）を表示しています"
            : $"バリエーション「{_viewVariation}」を表示しています。一覧の目のマークで、このバリエーションに入れる・外すを切り替えられます。足したオブジェクトはこのバリエーションだけに入ります");
    }

    // ---- 一覧の操作 ----

    private string DefaultVariationFile(string name)
    {
        var baseName = _path != null ? Path.GetFileNameWithoutExtension(_path).TrimStart('_').Replace("_master", "") : "panel";
        var ext = _path != null ? Path.GetExtension(_path) : ".txt";
        return $"{baseName}_{name}{ext}";
    }

    private string UniqueVariationName(string baseName)
    {
        var used = _doc!.VariationNames.ToHashSet();
        if (!used.Contains(baseName)) return baseName;
        for (int n = 2; ; n++) if (!used.Contains($"{baseName}_{n}")) return $"{baseName}_{n}";
    }

    /// <summary>
    /// 今の表示状態をバリエーションとして足す。今パネル画面に出ているオブジェクト（目のマークで隠していないもの。
    /// バリエーションを表示中なら、そのバリエーションに入っていて隠していないもの）が入る。
    /// </summary>
    private void OnAddVariation()
    {
        if (_doc == null) return;
        FlushReparse();
        var name = UniqueVariationName($"バリエーション{_doc.Variations.Count + 1}");
        var shown = Enumerable.Range(0, _doc.Sections.Count)
            .Where(i => !_doc.Sections[i].ViewHidden && (_viewVariation == null || _doc.Sections[i].InVariation(_viewVariation))).ToList();
        var others = Enumerable.Range(0, _doc.Sections.Count).Except(shown).ToList();
        bool first = _doc.Variations.Count == 0;
        _doc.Variations.Add(new PanelVariation { Name = name, File = DefaultVariationFile(name) });
        // 表示していないオブジェクトはこのバリエーションに入れない（1 つ目なら「どのバリエーションにも入らない」になり、問題の欄に出る。あとで足すバリエーションで使える）
        _doc.SetMembership(shown, name, true);
        _doc.SetMembership(others, name, false);
        CommitModel();
        SelectVariationRow(_doc.Variations.Count - 1);
        Message($"今の表示状態をバリエーション「{name}」として足しました（オブジェクト {shown.Count} 個{(others.Count > 0 ? $"。入らないオブジェクト {others.Count} 個" : "")}）。名前と出力ファイルを直してください"
            + (first && others.Count > 0 ? "。隠したオブジェクトは、ほかのバリエーションを足すときに表示して使えます" : ""));
        _varName.Focus();
        _varName.SelectAll();
    }

    private void OnDuplicateVariation()
    {
        if (_doc == null) return;
        if (_varList.SelectedIndex == 0)
        {
            // マスターの行の複製: すべてのオブジェクトが入るバリエーションを足す
            FlushReparse();
            var all = UniqueVariationName("すべて");
            _doc.Variations.Add(new PanelVariation { Name = all, File = DefaultVariationFile(all) });
            _doc.SetMembership(Enumerable.Range(0, _doc.Sections.Count), all, true);
            CommitModel();
            SelectVariationRow(_doc.Variations.Count - 1);
            Message($"マスターを「{all}」として複製しました（すべてのオブジェクトが入ります）");
            return;
        }
        if (SelectedVariation is not { } src) return;
        FlushReparse();
        var name = UniqueVariationName(src.Name);
        _doc.Variations.Insert(_doc.Variations.IndexOf(src) + 1, new PanelVariation { Name = name, File = DefaultVariationFile(name) });
        // 元のバリエーションに入る、条件付きのオブジェクトを新しいバリエーションにも入れる
        var members = Enumerable.Range(0, _doc.Sections.Count).Where(i => _doc.Sections[i].VariationCond != null && _doc.Sections[i].InVariation(src.Name)).ToList();
        _doc.SetMembership(members, name, true);
        CommitModel();
        SelectVariationRow(_doc.VariationNames.IndexOf(name));
        Message($"バリエーション「{src.Name}」を「{name}」として複製しました（同じオブジェクトが入ります）");
    }

    private void OnDeleteVariation()
    {
        if (_doc == null || SelectedVariation is not { } v) return;
        FlushReparse();
        // そのバリエーションだけに入っているオブジェクト
        var only = Enumerable.Range(0, _doc.Sections.Count).Where(i => _doc.Sections[i].VariationCond != null
            && _doc.MembershipOf(i) is { Count: 1 } m && m[0] == v.Name).ToList();
        int pick = 1;
        if (only.Count > 0)
        {
            pick = ChoiceDialog.Show(this, "バリエーションの削除",
                $"バリエーション「{v.Name}」だけに入っているオブジェクトが {only.Count} 個あります。どうしますか？",
                [$"そのオブジェクトも消す（{only.Count} 個）", "そのオブジェクトは残して共通にする"]);
            if (pick < 0) return;
        }
        else if (!Confirm($"バリエーション「{v.Name}」を一覧から消しますか？（Ctrl+Z で戻せます）")) return;
        _doc.DeleteVariation(v.Name);
        if (pick == 0) foreach (var i in only.OrderDescending()) _doc.Sections.RemoveAt(i);
        else foreach (var i in only) _doc.Sections[i].VariationCond = null;
        if (_viewVariation == v.Name) _viewVariation = null;
        CommitModel();
        Message($"バリエーション「{v.Name}」を消しました" + (only.Count > 0 ? (pick == 0 ? $"（そのバリエーションだけのオブジェクト {only.Count} 個も消しました）" : $"（そのバリエーションだけのオブジェクト {only.Count} 個は共通にしました）") : ""));
    }

    private void MoveVariation(int delta)
    {
        if (_doc == null || SelectedVariation is not { } v) return;
        int i = _doc.Variations.IndexOf(v), j = i + delta;
        if (j < 0 || j >= _doc.Variations.Count) return;
        FlushReparse();
        (_doc.Variations[i], _doc.Variations[j]) = (_doc.Variations[j], _doc.Variations[i]);
        // 条件の名前の並びも一覧の順に（入るバリエーションは変えない）
        foreach (var k in Enumerable.Range(0, _doc.Sections.Count).Where(k => _doc.Sections[k].VariationCond != null && VariationCondition.Parse(_doc.Sections[k].VariationCond)!.All(t => !t.Neg)).ToList())
            _doc.SetMembership([k], v.Name, _doc.Sections[k].InVariation(v.Name));
        CommitModel();
        SelectVariationRow(j);
    }

    private void CommitVariationName()
    {
        if (_doc == null || SelectedVariation is not { } v) return;
        var name = _varName.Text.Trim();
        if (name == v.Name) return;
        if (!VariationCondition.IsValidName(name))
        {
            Message("バリエーション名に、空白と , ! ; # [ ] = 、 は使えません（「なし」「共通」も使えません）");
            _varName.Text = v.Name;
            return;
        }
        if (_doc.VariationNames.Contains(name)) { Message($"「{name}」はもうあります"); _varName.Text = v.Name; return; }
        FlushReparse();
        var old = v.Name;
        bool defaultFile = v.File == DefaultVariationFile(old);
        _doc.RenameVariation(old, name);
        if (defaultFile) v.File = DefaultVariationFile(name); // 出力ファイルが初期値のままなら、名前に合わせる
        if (_viewVariation == old) _viewVariation = name;
        CommitModel();
        Message($"バリエーション名を「{old}」から「{name}」にしました（オブジェクトの条件も直しました）");
    }

    private void CommitVariationFile()
    {
        if (_doc == null || SelectedVariation is not { } v) return;
        var file = _varFile.Text.Trim();
        if (file == v.File) return;
        FlushReparse();
        v.File = file;
        CommitModel();
    }

    private void OnPickVariationFile()
    {
        if (_doc == null || SelectedVariation is not { } v) return;
        if (_path == null) { Message("先にマスターパネルを保存してください（出力ファイルはマスターのフォルダからの場所で書きます）"); return; }
        var masterDir = Path.GetDirectoryName(_path)!;
        var dlg = new SaveFileDialog
        {
            AddToRecent = false, Title = $"バリエーション「{v.Name}」の出力ファイル", OverwritePrompt = false,
            Filter = "パネルファイル|*.txt;*.cfg|すべて|*.*",
            InitialDirectory = Path.GetDirectoryName(PanelDocument.VariationOutputPath(_path, v)) is { } d && Directory.Exists(d) ? d : masterDir,
            FileName = Path.GetFileName(v.File.Length > 0 ? v.File : DefaultVariationFile(v.Name)),
        };
        if (dlg.ShowDialog(this) != true) return;
        _varFile.Text = Path.GetRelativePath(masterDir, dlg.FileName);
        CommitVariationFile();
    }

    // ---- 書き出し ----

    /// <summary>
    /// バリエーションのパネルファイルを書き出す。マスターを保存してから、入らないオブジェクトを消したパネルを出力ファイルに書く。
    /// 文字コード・改行はマスターのまま。すでにあるファイルは確認してから上書きする（最初の上書きでは .bak を残す）。戻り値は書き出したファイル。
    /// </summary>
    public List<string> ExportVariations(IReadOnlyList<PanelVariation> vars)
    {
        var written = new List<string>();
        if (_doc == null || vars.Count == 0) return written;
        FlushReparse();
        if (_path == null || IsDirty)
        {
            if (!IsTestRun && !Confirm("書き出す前にマスターパネルを保存します。よろしいですか？")) return written;
            if (!Save(_path)) return written;
        }
        var masterDir = Path.GetDirectoryName(_path!)!;
        var bad = vars.Where(v => v.File.Trim().Length == 0).Select(v => v.Name).ToList();
        if (bad.Count > 0) { Warn($"出力ファイルが決まっていないバリエーションがあります: {string.Join("、", bad)}"); return written; }
        var targets = vars.Select(v => (V: v, Path: PanelDocument.VariationOutputPath(_path!, v))).ToList();
        // 出力ファイルがほかのバリエーションと同じなら書き出さない（後のものが前のものを上書きしてしまう）
        var clash = targets.Where(t => _doc.Variations.Any(o => o != t.V && string.Equals(PanelDocument.VariationOutputPath(_path!, o), t.Path, StringComparison.OrdinalIgnoreCase)))
            .Select(t => t.V.Name).ToList();
        if (clash.Count > 0)
        {
            var text = $"出力ファイルがほかのバリエーションと同じなので書き出せません: {string.Join("、", clash)}。出力ファイルを別々にしてください。";
            if (IsTestRun) Message(text); else Warn(text);
            return written;
        }
        if (targets.FirstOrDefault(t => string.Equals(t.Path, Path.GetFullPath(_path!), StringComparison.OrdinalIgnoreCase)) is { V: not null } same)
        {
            Warn($"バリエーション「{same.V.Name}」の出力ファイルがマスターパネルと同じです。別の名前にしてください。");
            return written;
        }
        if (_doc.UnknownVariationNames() is { Count: > 0 } unknown)
            Message($"一覧にないバリエーション名（{string.Join("、", unknown)}）のオブジェクトは、どのバリエーションにも入りません");
        var exists = targets.Where(t => File.Exists(t.Path)).ToList();
        if (exists.Count > 0 && !Confirm($"次のファイルを上書きします（最初の上書きでは .bak を残します）。\n\n{string.Join("\n", exists.Select(t => "・" + Path.GetRelativePath(masterDir, t.Path)))}\n\n続けますか？"))
            return written;
        foreach (var (v, path) in targets)
        {
            try
            {
                var text = _doc.ExportVariation(v.Name, masterDir, Path.GetDirectoryName(path));
                var enc = BveText.ResolveSaveEncoding(text, _info);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && _backedUp.Add(path)) File.Copy(path, path + ".bak", overwrite: true);
                AtomicFile.WriteAllBytes(path, BveText.Encode(text, enc, _info.HasBom));
                written.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Warn($"バリエーション「{v.Name}」を書き出せませんでした:\n{ex.Message}");
            }
        }
        Message($"{written.Count} 個のバリエーションを書き出しました: {string.Join("、", written.Select(p => Path.GetRelativePath(masterDir, p)))}");
        return written;
    }

    // ---- 既存のパネルからマスターを作る ----

    private void OnImportVariations()
    {
        if (!ConfirmDiscard()) return;
        var w = new VariationImportWindow(this);
        if (w.ShowDialog() != true || w.Sources.Count == 0) return;
        ImportVariations(w.Sources, w.MasterPath);
    }

    /// <summary>既存のパネルからマスターを作って保存し、開く。戻り値は取り込みの結果（失敗なら null）。</summary>
    public VariationMergeResult? ImportVariations(IReadOnlyList<(string Name, string Path)> files, string masterPath)
    {
        // パネルファイルだけを取り込む
        var notPanel = files.Select(f => (f.Path, Why: VariationImportWindow.NotPanelReason(f.Path))).Where(x => x.Why != null).ToList();
        if (notPanel.Count > 0)
        {
            var refuse = "パネルファイルではないので取り込めません:\n" + string.Join("\n", notPanel.Select(x => $"・{Path.GetFileName(x.Path)}（{x.Why}）"));
            if (IsTestRun) Message(refuse.Replace("\n", " ")); else Warn(refuse);
            return null;
        }
        var sources = new List<VariationSource>();
        TextFileInfo? info = null;
        try
        {
            foreach (var (name, path) in files)
            {
                var (text, fi) = BveText.Read(path);
                info ??= fi;
                sources.Add(new VariationSource(name, path, text));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"パネルファイルを読めませんでした:\n{ex.Message}");
            return null;
        }
        var result = VariationMerge.Merge(sources, masterPath);
        if (File.Exists(masterPath) && !IsTestRun && !Confirm($"「{Path.GetFileName(masterPath)}」はもうあります。上書きしますか？")) return null;
        var masterText = result.Master.Serialize();
        var enc = BveText.ResolveSaveEncoding(masterText, info!);
        try { AtomicFile.WriteAllBytes(masterPath, BveText.Encode(masterText, enc, info!.HasBom)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"マスターパネルを保存できませんでした:\n{ex.Message}");
            return null;
        }
        _viewVariation = null;
        LoadPanel(masterPath, null);
        var msg = $"マスターパネル「{Path.GetFileName(masterPath)}」を作りました。共通のオブジェクト {result.Common} 個、一部のバリエーションだけのオブジェクト {result.Partial} 個です。";
        if (result.Mismatches.Count > 0) msg += $"\n\n書き出し直すと元と違うバリエーションがあります: {string.Join("、", result.Mismatches)}";
        else msg += "\n\n各バリエーションを書き出し直すと、元のパネルと同じオブジェクト・値・並びになることを確かめました。";
        if (result.Notes.Count > 0) msg += "\n\n" + string.Join("\n", result.Notes.Select(n => "・" + n));
        if (!IsTestRun) MessageBox.Show(this, msg, "既存のパネルからマスターを作る", MessageBoxButton.OK, MessageBoxImage.Information);
        Message($"マスターパネルを作りました（共通 {result.Common} 個・一部だけ {result.Partial} 個）");
        RightTabs.SelectedIndex = RightTabs.Items.Count - 1;
        return result;
    }
}
