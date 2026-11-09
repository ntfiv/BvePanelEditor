using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BvePanelEditor.Core;
using BvePanelEditor.Rendering;
using BvePanelEditor.Views;
using Microsoft.Win32;

namespace BvePanelEditor;

public sealed class ElementRow
{
    public int Index { get; init; }
    public string Kind { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Title { get; init; } = "";
    public bool HasTitle => Title.Length > 0;
    /// <summary>1 行目: 名前があれば名前、なければ種類。</summary>
    public string Primary => HasTitle ? Title : Kind;
    /// <summary>1 行目の Subject（名前があるときは 2 行目に回す）。</summary>
    public string PrimarySubject => HasTitle ? "" : Subject;
    public Visibility SecondaryVisibility => HasTitle && !Compact ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>1 行表示（名前だけ。名前がなければ種類と状態量）。</summary>
    public bool Compact { get; init; }
    public string Layer { get; init; } = "";
    public Brush KindBrush { get; init; } = Brushes.Gray;
    public Visibility ProblemVisibility { get; init; } = Visibility.Collapsed;
    /// <summary>BveEX の状態量（ats256 以上）を使っている計器。</summary>
    public Visibility BveExVisibility { get; init; } = Visibility.Collapsed;
    /// <summary>パネルくんの画面で隠している（グループの見出しなら、中の計器が全部）。</summary>
    public bool Hidden { get; init; }
    /// <summary>バリエーションを表示している（目のマークは「このバリエーションに入れる／外す」）。</summary>
    public bool VariationMode { get; init; }
    /// <summary>表示中のバリエーションに入る（グループの見出しなら、中の計器が全部）。</summary>
    public bool InVariation { get; init; } = true;
    /// <summary>入るバリエーション（共通なら空）。一覧の行の右に小さく出す。</summary>
    public string VariationText { get; init; } = "";
    public Visibility VariationTextVisibility => VariationText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string VariationTip => IsHeader ? $"グループの中の計器が全部入っているバリエーション: {VariationText}" : $"入るバリエーション: {VariationText}";
    private bool EyeClosed => VariationMode ? !InVariation : Hidden;
    /// <summary>目の印（Segoe MDL2 Assets: 表示 E890・非表示 ED1A）。</summary>
    public string Eye => EyeClosed ? "\uED1A" : "\uE890";
    public Brush EyeBrush => VariationMode ? (InVariation ? VariationEyeBrush : Brushes.Silver) : Hidden ? Brushes.Silver : Brushes.DimGray;
    public string EyeTip => VariationMode
        ? (IsHeader ? "クリックで、グループの計器をまとめて、表示中のバリエーションに入れる／外す" : "クリックで、表示中のバリエーションに入れる／外す（ファイルの「; @group」に書きます）")
        : (IsHeader ? "グループの計器をまとめて表示／非表示（パネルくんの画面だけ。ファイルには書きません）" : "表示／非表示（パネルくんの画面だけ。ファイルには書かず、BVE では表示されます）");
    public double RowOpacity => Hidden || (VariationMode && !InVariation) ? 0.45 : 1;
    private static readonly Brush VariationEyeBrush = MakeFrozen(Color.FromRgb(0x1B, 0x5F, 0xC1));
    /// <summary>コメントアウトして無効にした計器（BVE でも表示されない）。背景を灰色にする。</summary>
    public bool Disabled { get; init; }
    /// <summary>メインパネル・[This]（パネルの土台）。背景をごく薄い赤にする。</summary>
    public bool IsBase { get; init; }
    public Brush RowBackground => Disabled ? DisabledBackground : IsBase ? BaseBackground : Brushes.Transparent;
    public Visibility DisabledVisibility => Disabled ? Visibility.Visible : Visibility.Collapsed;
    private static readonly Brush DisabledBackground = MakeFrozen(Color.FromRgb(0xEE, 0xEE, 0xEE));
    private static readonly Brush BaseBackground = MakeFrozen(Color.FromRgb(0xFF, 0xF1, 0xF1));
    private static Brush MakeFrozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    // グループの見出し行（Index は -1）
    public bool IsHeader { get; init; }
    public string GroupName { get; init; } = "";
    public string GroupCount { get; init; } = "";
    public string Fold { get; init; } = "▼";
    public Visibility HeaderVisibility => IsHeader ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ItemVisibility => IsHeader ? Visibility.Collapsed : Visibility.Visible;
    /// <summary>グループの中の計器は字下げする（入れ子の深さだけ）。</summary>
    public Thickness Indent { get; init; }
    /// <summary>グループの見出しの字下げ（外側のグループの数だけ）。</summary>
    public Thickness HeaderMargin { get; init; } = new(0, 2, 0, 1);
}

public sealed class ProblemRow
{
    public int Section { get; init; }
    public string Text { get; init; } = "";
    /// <summary>BveEX 対応車両の知らせ（警告とは別の印）。</summary>
    public bool BveEx { get; init; }
    /// <summary>カーソルを載せたときの説明（なければ本文）。</summary>
    public string? TipText { get; init; }
    public string Tip => TipText ?? Text;
    public string Mark => BveEx ? "EX" : "⚠";
    public Brush MarkBrush => BveEx ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xD0, 0x80, 0x00));
    public Brush MarkBackground => BveEx ? new SolidColorBrush(Color.FromRgb(0x2A, 0x6F, 0xC9)) : Brushes.Transparent;
}

public partial class MainWindow : Window
{
    private PanelDocument? _doc;
    private string? _path;
    private TextFileInfo _info = TextFileInfo.NewUtf8();
    private VehicleInfo? _vehicle;
    private PanelScene? _scene;
    private readonly ImageCache _cache = new();
    private string _savedText = "";
    private bool _suppressText;
    private bool _suppressCaret;
    private bool _suppressList;
    private int _selected = -1;                  // 主に選んでいる計器
    private List<int> _selSet = [];              // 選んでいる計器すべて
    private string? _selGroup;                   // グループごと選んでいるときのグループ名
    private readonly HashSet<string> _folded = []; // 一覧で折りたたんでいるグループ
    private readonly HashSet<string> _backedUp = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _reparseTimer;
    private readonly PropertyEditor _props;
    private readonly StatePanel _states;
    private readonly SectionHighlighter _highlighter = new();
    private GridLength _textHeight = new(260);

    /// <summary>アプリの名前（画面に出す名前）。</summary>
    public const string AppName = "パネルくん";

    private static readonly string SettingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BvePanelEditor");
    private static readonly string RecentFile = Path.Combine(SettingsDir, "recent.txt");

    public MainWindow()
    {
        InitializeComponent();

        Editor.SyntaxHighlighting = PanelHighlighting.Definition;
        Editor.TextArea.TextView.BackgroundRenderers.Add(_highlighter);
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.ConvertTabsToSpaces = false;
        Editor.TextChanged += OnEditorTextChanged;
        Editor.TextArea.Caret.PositionChanged += OnCaretMoved;
        Editor.TextArea.LostKeyboardFocus += (_, _) => FlushReparse();

        _reparseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _reparseTimer.Tick += (_, _) => FlushReparse();

        _props = new PropertyEditor(PropHost, this);
        _props.Commit += OnPropertyCommit;
        _props.TitleCommit += OnTitleCommit;
        _props.GroupRenameCommit += n => { if (_selGroup != null) RenameGroup(_selGroup, n); };
        _props.GroupLocationCommit += OnGroupLocationCommit;
        _props.UngroupRequested += () => OnUngroup(null, null);
        _props.DuplicateGroupRequested += () => OnDuplicate(null, null);
        _props.MakeGroupRequested += () => OnMakeGroup(null, null);
        BuildVariationTab();
        _props.MultiCommit += OnMultiCommit;
        _props.AlignRequested += Align;
        _props.LayerRequested += ChangeLayers;
        _props.ViewLimitRequested += () => OpenViewLimit(null);
        _props.MainPanelPicked += OnMainPanelPicked;
        _props.ResolutionFromMainRequested += OnResolutionFromMain;
        _props.ThisToMainRequested += () => ThisToMainPanel();
        _props.IntervalRequested += OnIntervalRequested;
        Canvas.ActivePointChanged += p => _props.SetPanelPoints(p == Rendering.PanelCanvas.PanelPoint.Both);
        _props.AlignPointsRequested += originToCenter =>
        {
            if (Canvas.AlignPointsX(originToCenter))
                Message(originToCenter ? "消失点の横の位置を中心に合わせました" : "中心の横の位置を消失点に合わせました");
        };
        _props.Show(null, -1);

        _states = new StatePanel(StateHost, Canvas.State);
        _states.Changed += () => Canvas.InvalidateVisual();
        _states.Update(null);

        Canvas.SelectionChanged += i => SelectSection(i, fromCanvas: true);
        Canvas.EditCommitted += _ => { CommitModel(); _props.UpdateValues(); };
        Canvas.PanelMouseMove += p => CoordText.Text = $"X {p.X:0}  Y {p.Y:0}";
        Canvas.ViewChanged += () => ZoomText.Text = $"{Canvas.Zoom * 100:0}%";
        Canvas.LockedEditAttempt += () =>
        {
            if (_variationMode) { Message("バリエーション編集モードでは計器の位置は動かせません。ほかのタブ（プロパティなど）に戻ると動かせます"); return; }
            Message("位置はロック中です。パネル画面左上の「🔒 位置ロック中」をクリック（または Ctrl+L）すると動かせます");
            FlashLockButton();
        };
        Canvas.ValueDragStarted += OnValueDragStarted;
        Canvas.ValueDragged += OnValueDragged;
        Canvas.ValueClicked += OnValueClicked;
        Canvas.Ticked += dt => { if (SweepCheck.IsChecked == true) _states.Sweep(dt); };

        PreviewKeyDown += OnWindowKeyDown;
        Drop += OnDropFile;
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Closing += OnClosingWindow;
        Loaded += (_, _) => { BuildRecentMenu(); InitViewOptions(); };
        UpdateTitle();
    }

    // ================= ファイル =================

    /// <summary>画像として扱う拡張子（開く・ドロップしたら「画像からパネルを作る」へ）。</summary>
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"];

    /// <summary>ファイルをどこから開いたか。</summary>
    public enum OpenSource
    {
        /// <summary>ファイル → 開く、ドラッグ＆ドロップなど（自分で選んだ）。</summary>
        Explicit,
        /// <summary>最近使ったファイル。</summary>
        Recent,
        /// <summary>起動したときに渡されたファイル（Windows のタスクバーの「最近使ったもの」、エクスプローラーなど）。</summary>
        Startup,
    }

    public void OpenPath(string path) => OpenPath(path, OpenSource.Explicit);

    public void OpenPath(string path, OpenSource source)
    {
        // 画像や PSD はパネルファイルとして開かず、画像からパネルを作る（PSD は読み込み方を選ぶ）
        var ext = Path.GetExtension(path).ToLowerInvariant();
        // その画像から作ったパネルがすでにあれば、作り直して上書きしないよう、先にそのパネルを開くか聞く
        bool? offered = ImageExtensions.Contains(ext) || ext == ".psd" ? OfferExistingPanel(path) : null;
        if (offered == true) return;
        if (ImageExtensions.Contains(ext))
        {
            // 最近使ったファイル・起動時に渡された画像は、うっかり作り直さないよう確認する（上でパネルを開くか聞いたときは除く）
            if (offered == null && source != OpenSource.Explicit && !ConfirmImageOpen(path, source)) return;
            StartCropPanel(path);
            return;
        }
        if (ext == ".psd") { OpenPsd(path); return; }
        if (!ConfirmDiscard()) return;
        try
        {
            var (text, _) = BveText.Read(path);
            switch (BveFiles.Detect(text))
            {
                case BveFileKind.Panel:
                    LoadPanel(path, null);
                    break;
                case BveFileKind.Vehicle:
                    OpenVehicle(path, text);
                    break;
                case BveFileKind.Scenario:
                    {
                        var list = BveFiles.ScenarioVehicles(path, text);
                        if (list.Count == 0) { Warn("シナリオファイルに Vehicle がありません。"); return; }
                        int pick = list.Count == 1 ? 0 : ChoiceDialog.Show(this, "車両の選択", "このシナリオには車両が複数あります。開く車両を選んでください。", list);
                        if (pick < 0) return;
                        if (!File.Exists(list[pick])) { Warn($"車両ファイルがありません:\n{list[pick]}"); return; }
                        OpenVehicle(list[pick], BveText.Read(list[pick]).Text);
                        break;
                    }
                default:
                    if (MessageBox.Show(this, "BVE のパネルファイルの見出し（BveTs Instrument Panel）が見つかりません。\nパネルファイルとして開きますか？",
                            "確認", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                        LoadPanel(path, null);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"開けませんでした:\n{ex.Message}");
        }
    }

    private void OpenVehicle(string vehiclePath, string text)
    {
        var v = VehicleInfo.Load(vehiclePath, text);
        if (v.PanelPath == null) { Warn("車両ファイルに Panel の指定がありません。"); return; }
        if (!File.Exists(v.PanelPath)) { Warn($"車両ファイルが指すパネルファイルがありません:\n{v.PanelPath}"); return; }
        LoadPanel(v.PanelPath, v);
    }

    private void LoadPanel(string path, VehicleInfo? vehicle)
    {
        var (text, info) = BveText.Read(path);
        _path = Path.GetFullPath(path);
        _info = info;
        _vehicle = vehicle;
        _doc = PanelDocument.Parse(text);
        _selected = -1;
        _selSet = [];
        _selGroup = null;
        _folded.Clear();
        _cache.Clear();
        Canvas.ClearFreshEdit();
        CoverButton.Visibility = Visibility.Collapsed;
        SetEditorText(text, clearUndo: true);
        _savedText = text;
        RebuildAll();
        SetLocked(true);
        Dispatcher.BeginInvoke(() => Canvas.FitAll(), DispatcherPriority.Loaded);
        AddRecent(vehicle?.Path ?? _path);
        Message(info.EncodingFallback
            ? "文字コードの指定がなく UTF-8 として読めなかったので Shift_JIS で読みました（保存も Shift_JIS）"
            : $"{Path.GetFileName(path)} を開きました");
        UpdateTitle();
    }

    private void OnNew(object sender, ExecutedRoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var open = new OpenFileDialog { AddToRecent = false, Title = "パネルのベース画像を選ぶ", Filter = "画像|*.png;*.bmp;*.jpg;*.jpeg;*.gif|すべて|*.*" };
        if (open.ShowDialog(this) != true) return;
        var img = _cache.Get(open.FileName, null, null);
        if (img == null) { Warn("画像を読めませんでした。"); return; }
        var save = new SaveFileDialog
        {
            Title = "パネルファイルの保存先", Filter = PanelFilters.Save, FileName = "panel.txt",
            InitialDirectory = Path.GetDirectoryName(open.FileName),
        };
        if (save.ShowDialog(this) != true) return;
        var dir = Path.GetDirectoryName(save.FileName)!;
        // [This] には画像を書かず、選んだ画像はメインパネル（常表示の表示灯）にする
        var (cw, ch) = ContentSize(open.FileName) ?? (img.PixelWidth, img.PixelHeight);
        _doc = MainPanel.CreateNew(PropertyEditor.RelativePath(dir, open.FileName), null, cw, ch);
        _path = save.FileName;
        _info = TextFileInfo.NewUtf8();
        _vehicle = null;
        _selected = -1;
        _selSet = [];
        _selGroup = null;
        _folded.Clear();
        var text = _doc.Serialize();
        SetEditorText(text, clearUndo: true);
        _savedText = "";
        RebuildAll();
        SetLocked(true);
        Dispatcher.BeginInvoke(() => { Canvas.FitAll(); SelectSection(0); }, DispatcherPriority.Loaded);
        Message("新しいパネルを作りました。まだ保存していません（Ctrl+S で保存）");
        UpdateTitle();
    }

    private void OnOpen(object sender, ExecutedRoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = PanelFilters.Open };
        if (_path != null) dlg.InitialDirectory = Path.GetDirectoryName(_path);
        if (dlg.ShowDialog(this) == true) OpenPath(dlg.FileName);
    }

    private void HasDoc(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _doc != null;

    private void OnSave(object sender, ExecutedRoutedEventArgs e) => Save(_path);

    private void OnSaveAs(object sender, ExecutedRoutedEventArgs e) => SaveAs();

    private bool SaveAs()
    {
        if (_doc == null) return false;
        var dlg = new SaveFileDialog
        {
            Filter = PanelFilters.Save, FileName = _path != null ? Path.GetFileName(_path) : "panel.txt",
            // 今のファイルが .cfg なら .cfg を選んだ状態で開く
            FilterIndex = PanelFilters.IndexFor(_path),
        };
        if (_path != null) dlg.InitialDirectory = Path.GetDirectoryName(_path);
        if (dlg.ShowDialog(this) != true) return false;
        return SaveAsTo(dlg.FileName);
    }

    /// <summary>別の場所に保存する。フォルダが変わるなら画像と PSD の相対パスを直す。</summary>
    private bool SaveAsTo(string newPath)
    {
        if (_doc == null) return false;
        var oldDir = _path != null ? Path.GetDirectoryName(_path) : null;
        var newDir = Path.GetDirectoryName(Path.GetFullPath(newPath))!;
        if (oldDir != null && !string.Equals(Path.GetFullPath(oldDir), Path.GetFullPath(newDir), StringComparison.OrdinalIgnoreCase))
        {
            // 画像の相対パスを新しい場所から見た形に直す
            FlushReparse();
            int n = 0;
            foreach (var s in _doc.Sections)
                foreach (var key in new[] { "DaytimeImage", "NighttimeImage" })
                {
                    var rel = s.Get(key);
                    if (string.IsNullOrWhiteSpace(rel)) continue;
                    var full = Path.GetFullPath(Path.Combine(oldDir, rel.Trim().Trim('"')));
                    var nrel = PropertyEditor.RelativePath(newDir, full);
                    if (nrel != rel) { s.Set(key, nrel); n++; }
                }
            // PSD とのつながりのパスも新しい場所から見た形に直す
            foreach (var src in _doc.PsdSources)
            {
                var d = PropertyEditor.RelativePath(newDir, Path.GetFullPath(Path.Combine(oldDir, src.Day)));
                if (src.Day.Length > 0 && d != src.Day) { src.Day = d; n++; }
                if (src.Night != null)
                {
                    var nn = PropertyEditor.RelativePath(newDir, Path.GetFullPath(Path.Combine(oldDir, src.Night)));
                    if (nn != src.Night) { src.Night = nn; n++; }
                }
            }
            // 切り抜き元のパスも
            foreach (var c in _doc.CropSets)
            {
                var d = PropertyEditor.RelativePath(newDir, Path.GetFullPath(Path.Combine(oldDir, c.Day)));
                if (c.Day.Length > 0 && d != c.Day) { c.Day = d; n++; }
                if (c.Night != null)
                {
                    var nn = PropertyEditor.RelativePath(newDir, Path.GetFullPath(Path.Combine(oldDir, c.Night)));
                    if (nn != c.Night) { c.Night = nn; n++; }
                }
            }
            if (n > 0)
            {
                CommitModel();
                Message($"保存先のフォルダが変わったので、画像・PSD のパス {n} か所を直しました");
            }
        }
        return Save(newPath);
    }

    private bool Save(string? path)
    {
        if (_doc == null) return false;
        if (path == null) return SaveAs();
        FlushReparse();
        var text = Editor.Text;
        var enc = BveText.ResolveSaveEncoding(text, _info);
        var bytes = BveText.Encode(text, enc, _info.HasBom);
        if (enc.GetString(enc.GetBytes(text)) != text)
        {
            if (MessageBox.Show(this, $"文字コード {enc.WebName} では表せない文字があり、保存すると「?」に変わります。\n保存しますか？\n（1 行目を「{PanelDocument.HeaderPrefix} 2.00:utf-8」などにすると避けられます）",
                    "文字コード", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return false;
        }
        try
        {
            if (File.Exists(path) && _backedUp.Add(path))
                File.Copy(path, path + ".bak", overwrite: true);
            File.WriteAllBytes(path, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"保存できませんでした:\n{ex.Message}");
            return false;
        }
        bool moved = !string.Equals(_path, path, StringComparison.OrdinalIgnoreCase);
        _path = Path.GetFullPath(path);
        _savedText = text;
        ForgetPendingFiles(); // 書き出した画像は、保存したパネルが使う
        if (moved) { _cache.Clear(); RebuildAll(); }
        AddRecent(_path); // 画像から作ったばかりのパネルも、保存したら最近使ったファイルに入れる
        if (_doc != null && _doc.CropSets.Count > 0) RememberCropPanel(_path);
        Message($"保存しました（{enc.WebName}）。初回の保存では元のファイルを .bak に残しています");
        UpdateTitle();
        return true;
    }

    private bool IsDirty => _doc != null && Editor.Text != _savedText;

    /// <summary>予期しないエラーのときの控え: まだ保存していない内容とファイル名（保存済みなら null）。</summary>
    public (string Text, string Name)? RecoveryContent() => IsDirty ? (Editor.Text, _path != null ? Path.GetFileName(_path) : "新しいパネル.txt") : null;

    private bool ConfirmDiscard()
    {
        if (IsDirty)
        {
            var r = MessageBox.Show(this, "変更が保存されていません。保存しますか？", "確認", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return false;
            if (r == MessageBoxResult.Yes) return Save(_path);
        }
        // 保存しないで閉じる: このパネルのために書き出した画像が残っていれば、ゴミ箱に送るか聞く
        return AskTrashPendingFiles();
    }

    private void OnClosingWindow(object? sender, CancelEventArgs e)
    {
        if (!ConfirmDiscard()) e.Cancel = true;
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnReloadImages(object? sender, RoutedEventArgs? e)
    {
        _cache.Clear();
        RebuildAll();
        Message("画像を読み込み直しました");
    }

    // ---- 最近使ったファイル ----

    private static List<string> LoadRecent()
    {
        try { return File.Exists(RecentFile) ? File.ReadAllLines(RecentFile).Where(l => l.Length > 0).ToList() : []; }
        catch (IOException) { return []; }
    }

    /// <summary>確認用の起動（--selftest など）。最近使ったファイルを書き換えない。</summary>
    public static bool IsTestRun { get; set; }

    private void AddRecent(string path)
    {
        if (IsTestRun) return;
        var list = LoadRecent();
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllLines(RecentFile, list.Take(12));
        }
        catch (IOException) { }
        BuildRecentMenu();
    }

    private void BuildRecentMenu()
    {
        RecentMenu.Items.Clear();
        var list = LoadRecent();
        foreach (var p in list)
        {
            var item = new MenuItem { Header = PathDisplay.Show(p).Replace("_", "__") };
            var path = p;
            item.Click += (_, _) => OpenPath(path, OpenSource.Recent);
            RecentMenu.Items.Add(item);
        }
        RecentMenu.IsEnabled = list.Count > 0;
    }

    // ================= モデルとテキストの同期 =================

    private void SetEditorText(string text, bool clearUndo)
    {
        _suppressText = true;
        Editor.Document.Text = text;
        _suppressText = false;
        if (clearUndo) Editor.Document.UndoStack.ClearAll();
    }

    /// <summary>モデルの変更をテキストに反映する（変わった部分だけ置き換えるので、元に戻すが 1 回で効く）。</summary>
    private void CommitModel()
    {
        if (_doc == null) return;
        var newText = _doc.Serialize();
        var old = Editor.Text;
        if (newText != old)
        {
            int start = 0;
            int max = Math.Min(old.Length, newText.Length);
            while (start < max && old[start] == newText[start]) start++;
            int endOld = old.Length, endNew = newText.Length;
            while (endOld > start && endNew > start && old[endOld - 1] == newText[endNew - 1]) { endOld--; endNew--; }
            _suppressText = true;
            Editor.Document.Replace(start, endOld - start, newText[start..endNew]);
            _suppressText = false;
        }
        RebuildAll();
        UpdateTitle();
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        UpdateTitle();
        if (_suppressText || _doc == null) return;
        _reparseTimer.Stop();
        _reparseTimer.Start();
    }

    /// <summary>テキストの編集を待たずにモデルへ反映する。</summary>
    private void FlushReparse()
    {
        if (!_reparseTimer.IsEnabled) return;
        _reparseTimer.Stop();
        if (_doc == null) return;
        var old = _doc;
        _doc = PanelDocument.Parse(Editor.Text);
        CarryHidden(old, _doc); // テキストを直しても、非表示にした計器は非表示のまま
        RebuildAll();
    }

    private void RebuildAll()
    {
        if (_doc == null)
        {
            Canvas.Clear();
            return;
        }
        var baseDir = _path != null ? Path.GetDirectoryName(_path) : null;
        if (_viewVariation != null && !_doc.VariationNames.Contains(_viewVariation)) _viewVariation = null;
        _scene = PanelScene.Build(_doc, baseDir, _cache, _scene, _viewVariation);
        NormalizeSelection();
        Canvas.SetScene(_doc, _scene);
        Canvas.SetSelection(_selected, _selSet, _selGroup, Canvas.EnteredGroup);
        _states.Update(_scene);

        _props.BaseDir = baseDir;
        _props.V201 = _doc.SupportsV201;

        var problemSections = _scene.Problems.Where(p => !p.BveEx).Select(p => p.Section).ToHashSet();
        var bveExSections = _scene.Problems.Where(p => p.BveEx).Select(p => p.Section).ToHashSet();
        var rows = new List<ElementRow>();
        bool blankThis = IsBlankThis();
        // レイヤー順: 手前（レイヤーが大きい）ほど上。同じレイヤーならファイルの後ろ（BVE で手前に描かれる）ほど上。[This] はいちばん下。
        // グループはひとかたまりのまま（いちばん手前の計器の位置に置き、中もレイヤー順）
        var order = _layerOrder ? LayerOrder() : Enumerable.Range(0, _doc.Sections.Count).ToList();
        for (int k = 0; k < order.Count; k++)
        {
            int i = order[k];
            var s = _doc.Sections[i];
            // 前の行と共通でないグループの見出しを、外側から順に出す（折りたたんだグループの中は出さない）
            var prev = k == 0 ? [] : _doc.Sections[order[k - 1]].Groups;
            int common = 0;
            while (common < prev.Count && common < s.Groups.Count && prev[common] == s.Groups[common]) common++;
            for (int d = common; d < s.Groups.Count; d++)
            {
                var g = s.Groups[d];
                if (s.Groups.Take(d).Any(_folded.Contains)) break;
                bool folded = _folded.Contains(g);
                var members = _doc.Members(g);
                rows.Add(new ElementRow
                {
                    Index = -1, IsHeader = true, GroupName = g, Fold = folded ? "▶" : "▼",
                    GroupCount = $"（{members.Count}）",
                    Hidden = members.Count > 0 && members.All(m => _doc.Sections[m].ViewHidden),
                    VariationMode = _viewVariation != null,
                    InVariation = _viewVariation == null || members.All(m => _doc.Sections[m].InVariation(_viewVariation)),
                    VariationText = GroupVariationText(members),
                    HeaderMargin = new Thickness(14 * d, 2, 0, 1),
                });
            }
            if (s.Groups.Any(_folded.Contains)) continue;
            rows.Add(new ElementRow
            {
                Index = i,
                // メインパネルは「メインパネル」と画像の名前、画像のない [This] は「[This]（画像なし）」（前の版の透明な画像なら「（透明）」）
                Kind = s.IsMainPanel && s.Kind == SectionKind.PilotLamp ? MainPanel.Title : s.Kind == SectionKind.This && blankThis ? "[This]" : PanelSpec.KindLabel(s.Kind),
                Subject = s.Kind == SectionKind.This ? (string.IsNullOrWhiteSpace(s.Get("DaytimeImage")) ? "（画像なし）" : blankThis ? "（透明）" : Path.GetFileName(s.Get("DaytimeImage") ?? ""))
                    : s.IsMainPanel ? Path.GetFileName(s.Get("DaytimeImage") ?? "") : SubjectLabel(s.Get("Subject")),
                Title = s.Title,
                Layer = s.Kind == SectionKind.This ? "" : $"L{s.Get("Layer") ?? "0"}",
                KindBrush = KindBrush(s),
                ProblemVisibility = problemSections.Contains(i) ? Visibility.Visible : Visibility.Collapsed,
                BveExVisibility = bveExSections.Contains(i) ? Visibility.Visible : Visibility.Collapsed,
                Hidden = s.ViewHidden,
                VariationMode = _viewVariation != null,
                InVariation = _viewVariation == null || s.InVariation(_viewVariation),
                Compact = _compactList,
                VariationText = s.VariationCond?.Replace(", ", "・") ?? "",
                Disabled = s.Disabled,
                IsBase = s.Kind == SectionKind.This || (s.IsMainPanel && s.Kind == SectionKind.PilotLamp),
                Indent = new Thickness(14 * s.Groups.Count, 0, 0, 0),
            });
        }
        _suppressList = true;
        ElementList.ItemsSource = rows;
        _suppressList = false;
        ShowSelectionDetails(scroll: false);

        // BveEX の状態量（ats256 以上）は警告にせず、「BveEX 対応計器」としていちばん上に 1 行でまとめる
        var warnings = _scene.Problems.Where(p => !p.BveEx).ToList();
        var problemRows = new List<ProblemRow>();
        // 1 行目の文字コードの指定と中身が違ったファイル（中身の文字コードで読み書きしている）。短く出し、説明はカーソルを載せたとき
        if (_info.HeaderEncoding != null)
        {
            var actual = BveText.DisplayName(_info.Encoding);
            problemRows.Add(new ProblemRow
            {
                Section = -1,
                Text = $"文字コード: 1 行目は「{_info.HeaderEncodingName}」、中身は {actual}（{actual} で読み書きします）",
                TipText = $"1 行目の文字コードの指定（{_info.HeaderEncodingName}）と、ファイルの中身（{actual}）が違います。"
                    + $"パネルくんは中身の {actual} で読み、保存も {actual} で書きます（ファイルの元の文字コードのまま）。"
                    + "BVE は 1 行目の指定で読むので、日本語のコメントやファイル名が化けるかもしれません。1 行目の「:」の後ろを直すことをおすすめします",
            });
        }
        if (bveExSections.Count > 0)
        {
            var nums = _scene.Problems.Where(p => p.BveEx && p.Section >= 0)
                .Select(p => Subjects.IsBveExAts(_doc.Sections[p.Section].Get("Subject") ?? "", out var n) ? n : -1).Where(n => n >= 0).ToList();
            var range = nums.Count == 0 ? "" : nums.Min() == nums.Max() ? $"ats{nums.Min()}" : $"ats{nums.Min()}〜ats{nums.Max()}";
            problemRows.Add(new ProblemRow
            {
                Section = bveExSections.Min(),
                BveEx = true,
                Text = $"BveEX 対応計器（{range}・{bveExSections.Count} 個）",
                TipText = $"BveEX 対応車両: {range} を {bveExSections.Count} 個の計器で使っています（BveEX の状態量。BVE 本体の ats は 0〜255 なので、BveEX なしでは動きません）。計器の一覧の「EX」の印の計器です",
            });
        }
        problemRows.AddRange(warnings.Select(p => new ProblemRow
        {
            Section = p.Section,
            Text = p.Section >= 0 ? $"#{p.Section + 1} {DisplayName(_doc.Sections[p.Section])}: {p.Message}" : p.Message,
        }));
        ProblemList.ItemsSource = problemRows;
        int warnCount = warnings.Count + (_info.HeaderEncoding != null ? 1 : 0);
        ProblemHeader.Text = (warnCount == 0 ? "問題なし" : $"問題 {warnCount} 件（ダブルクリックで移動）") + (bveExSections.Count > 0 ? "・BveEX 対応計器あり" : "");
        ProblemHeader.Foreground = warnCount == 0 ? Brushes.SeaGreen : Brushes.DarkOrange;
        ProblemIcon.Visibility = warnCount > 0 ? Visibility.Visible : Visibility.Collapsed;

        var encName = _info.HasBom ? $"{_info.Encoding.WebName} (BOM)" : BveText.ResolveSaveEncoding(Editor.Text, _info).WebName + (_info.HeaderEncoding != null ? "（1 行目の指定と違う）" : "");
        FormatText.Text = $"{(_doc.Version.Length > 0 ? "版 " + _doc.Version : _doc.IsLegacyHeader ? "版 1.0（古い書式）" : "版 ?")}  {encName}  {(_info.NewLine == "\n" ? "LF" : "CRLF")}"
            + (_vehicle != null ? $"  車両: {Path.GetFileName(_vehicle.Path)}" : "");
        CropUpdateMenu.IsEnabled = _doc.HasCropLink;
    }

    /// <summary>一覧に出す状態量（true は「常表示」）。</summary>
    private static string SubjectLabel(string? subject) =>
        string.Equals(subject?.Trim(), "true", StringComparison.OrdinalIgnoreCase) ? "常表示" : subject ?? "";

    private static string DisplayName(PanelSection s) =>
        s.Title.Length > 0 ? $"{s.Title}（{PanelSpec.KindLabel(s.Kind)}）" : $"{PanelSpec.KindLabel(s.Kind)} {SubjectLabel(s.Get("Subject"))}";

    /// <summary>一覧の色の区別。メインパネルは赤、常表示の表示灯（Subject = true）はグレー。</summary>
    private static Brush KindBrush(PanelSection s) =>
        s.IsMainPanel && s.Kind == SectionKind.PilotLamp ? Brushes.Red
        : s.Kind == SectionKind.PilotLamp && string.Equals(s.Get("Subject")?.Trim(), "true", StringComparison.OrdinalIgnoreCase)
            ? Brushes.DarkGray : KindBrush(s.Kind);

    private static Brush KindBrush(SectionKind k) => k switch
    {
        SectionKind.This => Brushes.DimGray,
        SectionKind.PilotLamp => Brushes.Orange,
        SectionKind.Needle => Brushes.RoyalBlue,
        SectionKind.DigitalNumber => Brushes.MediumSeaGreen,
        SectionKind.DigitalGauge => Brushes.MediumOrchid,
        _ => Brushes.DarkMagenta, // 読めない計器（赤はメインパネルに使う）
    };

    // ================= 選択 =================

    /// <summary>1 つだけ選ぶ（キャンバスからなら、キャンバスで決めた選択をそのまま使う）。</summary>
    private void SelectSection(int index, bool fromCanvas = false, bool fromText = false)
    {
        if (_doc == null) return;
        if (fromCanvas)
        {
            ApplySelection(Canvas.Selected, Canvas.SelectedSet.ToList(), Canvas.SelectedGroup, SelectionSource.Canvas);
            return;
        }
        ApplySelection(index, index >= 0 ? [index] : [], null, fromText ? SelectionSource.Text : SelectionSource.Other);
    }

    private void UpdateHighlight()
    {
        if (_doc != null && _selected >= 0 && _selected < _doc.Sections.Count)
        {
            var map = _doc.LineMap();
            var r = map[_selected];
            _highlighter.FirstLine = r.HeaderLine;
            _highlighter.HeaderLine = r.HeaderLine;
            _highlighter.LastLine = r.LastLine;
            if (_selGroup != null && _doc.RunOf(_selGroup) is { } run)
            {
                // グループごとの選択は、開始行から終了行まで
                _highlighter.FirstLine = map[run.First].FirstLine;
                _highlighter.HeaderLine = map[run.First].HeaderLine;
                _highlighter.LastLine = map[run.Last].LastLine;
            }
        }
        else _highlighter.FirstLine = 0;
        Editor.TextArea.TextView.InvalidateLayer(_highlighter.Layer);
    }

    /// <summary>テキストの line 行目がグループの開始行・終了行なら、そのグループの名前。</summary>
    private string? GroupOfLine(int line)
    {
        if (line < 1 || line > Editor.Document.LineCount) return null;
        var text = Editor.Document.GetText(Editor.Document.GetLineByNumber(line));
        return PanelDocument.TryGroupBegin(text, out var b) ? b : PanelDocument.TryGroupEnd(text, out var e) ? e : null;
    }

    private void OnCaretMoved(object? sender, EventArgs e)
    {
        if (_suppressCaret || _doc == null || !Editor.TextArea.IsKeyboardFocusWithin || _reparseTimer.IsEnabled) return;
        int line = Editor.TextArea.Caret.Line;
        int idx = _doc.SectionAtLine(line);
        // グループの開始行・終了行にカーソルがあればグループごと選ぶ
        var role = _doc.EnumerateTagged().Skip(line - 1).Select(t => t.Role).FirstOrDefault();
        if (idx >= 0 && role is LineRole.GroupBegin or LineRole.GroupEnd && GroupOfLine(line) is { } g && _doc.Members(g).Count > 0)
        {
            if (_selGroup != g) ApplySelection(_doc.Members(g)[0], _doc.Members(g), g, SelectionSource.Text);
            return;
        }
        if (idx != _selected || _selSet.Count != 1) SelectSection(idx, fromText: true);
    }

    private void OnProblemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProblemList.SelectedItem is ProblemRow { Section: >= 0 } p) SelectSection(p.Section);
    }

    // ================= 編集 =================

    /// <summary>
    /// 表示範囲を、パネル本体の描かれている範囲から計算する画面を開く。確認用に snapshot を渡すと、画面を保存して閉じる（結果は入れない）。
    /// </summary>
    public string? OpenViewLimit(string? snapshot, Action<Views.ViewLimitWindow>? prepare = null)
    {
        if (_doc?.This is not { } self || PanelDir == null) { Message("パネル本体（[This]）がありません"); return null; }
        FlushReparse();
        // メインパネルがあればその画像（位置どおりに置いたもの）から、なければ [This] の画像から
        var (day, night) = ViewLimitSource();
        if (day == null && night == null) { Warn(_doc.MainPanel != null ? "メインパネルの画像を読めませんでした" : "パネル本体の画像を読めませんでした"); return null; }
        double res = PanelValues.Double(self.Get("Resolution"), 0);
        int margin = res > 0 ? (int)Math.Round(res * Canvas.ShakeMarginPercent / 100) : 0;
        double sw = res, sh = res > 0 ? res / Math.Max(0.1, Canvas.TallestFrameAspect) : 0;
        var w = new Views.ViewLimitWindow(this, day, night, margin, sw, sh);
        if (snapshot != null)
        {
            w.Show();
            w.UpdateLayout();
            prepare?.Invoke(w);
            w.UpdateLayout();
            SaveWindowPng(w, snapshot);
            var info = w.InfoForTest;
            w.Close();
            return info;
        }
        if (w.ShowDialog() != true) return null;
        var r = w.Result;
        self.Set("Left", r.Left.ToString());
        self.Set("Top", r.Top.ToString());
        self.Set("Right", r.Right.ToString());
        self.Set("Bottom", r.Bottom.ToString());
        CommitModel();
        Message($"表示範囲を 左 {r.Left}・上 {r.Top}・右 {r.Right}・下 {r.Bottom} にしました（描かれている範囲 {w.DrawnArea.Width}×{w.DrawnArea.Height} から余白を引いたもの）");
        return null;
    }

    private void OnPropertyCommit(string key, string? value)
    {
        if (_doc == null || _selected < 0) return;
        FlushReparse();
        var sec = _doc.Sections[_selected];
        sec.Set(key, value);
        // デジタル数字の画像を選んだとき、1 コマの高さがまだなければ画像から入れる
        bool filled = false;
        if (sec.Kind == SectionKind.DigitalNumber && key.Equals("DaytimeImage", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(sec.Get("Interval")) && !string.IsNullOrWhiteSpace(value) && PanelDir != null
            && DetectInterval(Path.GetFullPath(Path.Combine(PanelDir, value.Trim().Trim('"')))) is { } r)
        {
            sec.Set("Interval", r.Interval.ToString());
            Message($"1 コマの高さ（Interval）を {r.Interval} にしました: {r.Reason}");
            filled = true;
        }
        CommitModel();
        if (filled) _props.UpdateValues();
    }

    /// <summary>デジタル数字の画像から 1 コマの高さを見つける（読めなければ null）。</summary>
    private static DigitalFramesResult? DetectInterval(string fullPath)
    {
        var px = Rendering.ImageLoader.TryLoadPixels(fullPath);
        if (px == null) return null;
        int? content = BvePanelEditor.Core.Psd.PngWriter.TryReadContentSize(fullPath, out _, out var ch) ? ch : null;
        return DigitalFrames.Detect(px, content);
    }

    /// <summary>右の欄の「1 コマの高さを画像から求める」。</summary>
    private void OnIntervalRequested()
    {
        if (_doc == null || _selected < 0 || PanelDir == null) return;
        FlushReparse();
        var sec = _doc.Sections[_selected];
        var rel = sec.Get("DaytimeImage") is { Length: > 0 } d ? d : sec.Get("NighttimeImage");
        if (string.IsNullOrWhiteSpace(rel)) { Message("画像（DaytimeImage）がありません"); return; }
        var r = DetectInterval(Path.GetFullPath(Path.Combine(PanelDir, rel.Trim().Trim('"'))));
        if (r == null) { Warn("画像から 1 コマの高さを見つけられませんでした。Interval を手で入れてください。"); return; }
        var old = sec.Get("Interval");
        // すでに値があって違うときは、置き換えてよいか聞く（推定は外れることがあるので）
        if (old is { Length: > 0 } && old.Trim() != r.Interval.ToString()
            && !Confirm($"画像からは 1 コマの高さを {r.Interval} と{(r.Confident ? "見つけました" : "推定しました")}。今の値は {old.Trim()} です。\n{r.Interval} に置き換えますか？\n\n{r.Reason}"))
            return;
        sec.Set("Interval", r.Interval.ToString());
        CommitModel();
        _props.UpdateValues();
        Message($"1 コマの高さ（Interval）を {r.Interval} にしました{(old is { Length: > 0 } && old.Trim() != r.Interval.ToString() ? $"（前は {old.Trim()}）" : "")}: {r.Reason}"
            + (r.Confident ? "" : "。動作プレビュー（Ctrl＋上下ドラッグ）で数字がずれないか確かめてください"));
    }

    // ---- 名前（直前のコメント） ----

    private void OnTitleCommit(string title)
    {
        if (_doc == null || _selected < 0) return;
        FlushReparse();
        var s = _doc.Sections[_selected];
        if (s.Title == title.Trim()) return;
        s.SetTitle(title);
        CommitModel();
        Message(title.Trim().Length == 0 ? "名前を消しました" : $"名前を「{title.Trim()}」にしました");
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2) { BeginRename(); e.Handled = true; }
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(ElementList, d) is ListBoxItem) BeginRename();
    }

    private void OnRenameMenu(object sender, RoutedEventArgs e) => BeginRename();

    /// <summary>名前を入力中の欄（確認用）。</summary>
    private TextBox? _renameBox;

    /// <summary>一覧の選択行の上に名前の入力欄を出す。</summary>
    private void BeginRename()
    {
        if (_doc == null || _selected < 0 || !IsVisible) return;
        var row = RowForSelection();
        if (row == null) return;
        ElementList.ScrollIntoView(row);
        ElementList.UpdateLayout();
        if (ElementList.ItemContainerGenerator.ContainerFromItem(row) is not ListBoxItem item) return;
        int index = _selected;
        string? group = row.IsHeader ? row.GroupName : null;
        var box = new TextBox
        {
            Text = group ?? _doc.Sections[index].Title, Width = Math.Max(120, item.ActualWidth - 8), FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(2), BorderBrush = Brushes.DodgerBlue, BorderThickness = new Thickness(2),
        };
        // 入力欄は行の上に重ねて、このウィンドウの中に描く（別のウィンドウ（Popup）にすると、日本語入力の変換の枠が画面の左上に出てしまう）
        var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(item);
        if (layer == null) return;
        var adorner = new EditBoxAdorner(item, box, 4, 1);
        bool done = false;
        void Finish(bool apply)
        {
            if (done) return;
            done = true;
            layer.Remove(adorner);
            _renameBox = null;
            if (apply && group != null) RenameGroup(group, box.Text);
            else if (apply && _selected == index) OnTitleCommit(box.Text);
            ElementList.Focus();
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Finish(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { Finish(false); e.Handled = true; }
        };
        box.LostKeyboardFocus += (_, _) => Finish(true);
        layer.Add(adorner);
        _renameBox = box;
        layer.UpdateLayout();
        box.Focus();
        box.SelectAll();
        Message(group != null
            ? "グループ名を入力して Enter（Esc で取り消し）。グループの開始行・終了行に書きます"
            : "名前を入力して Enter（Esc で取り消し）。見出しのすぐ上のコメント行として保存します");
    }

    private void OnAddButton(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var (kind, label) in new[] { (SectionKind.PilotLamp, "表示灯"), (SectionKind.Needle, "指針"), (SectionKind.DigitalNumber, "デジタル数字"), (SectionKind.DigitalGauge, "円形ゲージ") })
        {
            var mi = new MenuItem { Header = $"{label} [{kind}]", Tag = kind.ToString() };
            mi.Click += OnAddElement;
            menu.Items.Add(mi);
            if (kind == SectionKind.Needle)
            {
                var std = new MenuItem { Header = "指針（標準の針・角度を選んで）[Needle]…" };
                std.Click += OnAddStandardNeedle;
                menu.Items.Add(std);
                var cover = new MenuItem { Header = "指針の蓋（選んだ指針の上に）…", IsEnabled = CoverTargets().Count > 0 };
                cover.Click += OnMakeCover;
                menu.Items.Add(cover);
            }
        }
        menu.PlacementTarget = AddButton;
        menu.IsOpen = true;
    }

    private void OnAddElement(object sender, RoutedEventArgs e)
    {
        if (_doc == null || sender is not MenuItem { Tag: string tag } || !Enum.TryParse<SectionKind>(tag, out var kind)) return;
        FlushReparse();
        var baseDir = _path != null ? Path.GetDirectoryName(_path) : null;
        var sec = PanelSection.Create(kind);
        var c = Canvas.ViewCenterPanel;
        int layer = Math.Max(1, _scene?.Items.Select(i => i.Layer).DefaultIfEmpty(1).Max() ?? 1);

        double w = 0, h = 0;
        if (kind != SectionKind.DigitalGauge)
        {
            var dlg = new OpenFileDialog { AddToRecent = false, Title = $"{PanelSpec.KindLabel(kind)}の画像を選ぶ", Filter = "画像|*.png;*.bmp;*.jpg;*.jpeg;*.gif|すべて|*.*" };
            if (baseDir != null) dlg.InitialDirectory = baseDir;
            if (dlg.ShowDialog(this) != true) return;
            var img = _cache.Get(dlg.FileName, null, null);
            if (img == null) { Warn("画像を読めませんでした。"); return; }
            w = img.PixelWidth;
            h = img.PixelHeight;
            sec.Set("DaytimeImage", baseDir != null ? PropertyEditor.RelativePath(baseDir, dlg.FileName) : dlg.FileName);
        }

        switch (kind)
        {
            case SectionKind.PilotLamp:
                sec.Set("Subject", "ats0");
                sec.Set("Location", PanelValues.Point(Math.Round(c.X - w / 2), Math.Round(c.Y - h / 2)));
                break;
            case SectionKind.DigitalNumber:
                {
                    // 1 コマの高さは画像から見つける（見つからなければ 10 コマとみなす）
                    var found = DetectInterval(Path.GetFullPath(baseDir != null ? Path.Combine(baseDir, sec.Get("DaytimeImage")!) : sec.Get("DaytimeImage")!));
                    int interval = found?.Interval ?? (h >= 10 ? (int)(h / 10) : (int)h);
                    if (found != null) Message($"1 コマの高さ: {found.Reason}");
                    sec.Set("Subject", "power");
                    sec.Set("Location", PanelValues.Point(Math.Round(c.X - w / 2), Math.Round(c.Y - interval / 2.0)));
                    sec.Set("Interval", interval.ToString());
                    break;
                }
            case SectionKind.Needle:
                sec.Set("Subject", "kmph");
                sec.Set("Location", PanelValues.Point(Math.Round(c.X), Math.Round(c.Y)));
                sec.Set("Origin", PanelValues.Point(Math.Round(w / 2), Math.Round(h / 2)));
                sec.Set("Radius", PanelValues.Num(Math.Round(h / 2)));
                sec.Set("InitialAngle", "-135");
                sec.Set("LastAngle", "135");
                sec.Set("Minimum", "0");
                sec.Set("Maximum", "120");
                break;
            case SectionKind.DigitalGauge:
                sec.Set("Subject", "bc");
                sec.Set("Location", PanelValues.Point(Math.Round(c.X), Math.Round(c.Y)));
                sec.Set("Radius", "50");
                sec.Set("Color", "202020"); // 消灯の色
                sec.Set("InitialAngle", "-135");
                sec.Set("LastAngle", "135");
                sec.Set("Minimum", "0");
                sec.Set("Maximum", "600");
                break;
        }
        sec.Set("Layer", layer.ToString());

        int at = _selected >= 0 ? _selected + 1 : _doc.Sections.Count;
        if (_selGroup != null && _doc.RunOf(_selGroup) is { } run)
        {
            at = run.Last + 1;          // グループごと選んでいればグループの最後に入れる
            sec.SetGroups([.. _doc.ParentPath(_selGroup), _selGroup]);
        }
        else if (_selected >= 0) sec.SetGroups(_doc.Sections[_selected].Groups); // グループの中の計器の後ろなら同じグループに
        JoinViewVariation(sec);
        _doc.Sections.Insert(at, sec);
        _selected = at;
        CommitModel();
        SelectSection(at);
        Canvas.AllowFreshEdit([at]);
        Message($"{PanelSpec.KindLabel(kind)}を追加しました{ViewVariationNote}。Subject などを右の欄で直してください。追加した計器は、選んでいる間だけ位置ロック中でも動かせます（ほかを選ぶとロックに戻ります）");
        Dispatcher.BeginInvoke(BeginRename, DispatcherPriority.Loaded);
    }

    private void OnDuplicate(object? sender, RoutedEventArgs? e)
    {
        if (_doc == null || _selected < 0) return;
        FlushReparse();
        if (_selGroup != null) { DuplicateGroup(_selGroup); return; }
        if (_selSet.Count > 1) { Message("複製は 1 つの計器か、グループごと選んだときに使えます"); return; }
        var src = _doc.Sections[_selected];
        if (src.Kind == SectionKind.This) { Message("[This] は 1 つだけなので複製できません"); return; }
        var copy = src.Clone();
        copy.PsdLayers.Clear(); // 複製は PSD とつながらない（PSD から更新したとき元の位置へ戻されないように）
        copy.CropSet = null;    // 切り抜き元とも（元の表示灯と同じ PNG を指したままなので、更新で上書きされないように）
        copy.CropShapes.Clear();
        copy.IsMainPanel = false; // メインパネルは 1 つだけ
        copy.ViewHidden = false;
        if (PanelValues.TryPoint(copy.Get("Location"), out var x, out var y))
            copy.Set("Location", PanelValues.Point(x + 10, y + 10));
        if (copy.Leading.Count == 0 || copy.Leading[0].Kind != PanelLineKind.Blank) copy.Leading.Insert(0, PanelLine.Blank());
        JoinViewVariation(copy);
        _doc.Sections.Insert(_selected + 1, copy);
        _selected++;
        CommitModel();
        SelectSection(_selected);
        if (_viewVariation != null) Message($"複製しました{ViewVariationNote}");
    }

    private void OnDelete(object? sender, RoutedEventArgs? e)
    {
        if (_doc == null || _selected < 0) return;
        FlushReparse();
        if (_selSet.Count > 1 || _selGroup != null) { DeleteSelected(); return; }
        var s = _doc.Sections[_selected];
        if (s.Kind == SectionKind.This &&
            MessageBox.Show(this, "[This]（パネル本体）を削除しますか？", "確認", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _doc.Sections.RemoveAt(_selected);
        if (_selected >= _doc.Sections.Count) _selected = _doc.Sections.Count - 1;
        CommitModel();
        SelectSection(_selected);
        Message("削除しました（Ctrl+Z で元に戻せます）");
    }

    private void Move(int delta)
    {
        if (_doc == null || _selected < 0) return;
        FlushReparse();
        if (_selGroup != null)
        {
            var g = _selGroup;
            _doc.MoveGroup(g, delta);
            CommitModel();
            ApplySelection(_doc.Members(g)[0], _doc.Members(g), g, SelectionSource.Other);
            return;
        }
        if (_selSet.Count > 1) { Message("並べ替えは 1 つの計器か、グループごと選んだときに使えます"); return; }
        int to = _doc.MoveSection(_selected, delta);
        if (to == _selected)
        {
            if (_doc.Sections[_selected].Group != null) Message("グループの中の計器はグループの中だけで動きます（外すときは「グループから外す」）");
            return;
        }
        _selected = to;
        CommitModel();
        SelectSection(to);
    }

    // レイヤー順のときは ↑↓ でレイヤーを上げ下げする（一覧の上ほど手前なので、↑ = レイヤーを上げる）
    private void OnAbout(object sender, RoutedEventArgs e) => new AboutWindow(this).ShowDialog();

    private void OnMoveUp(object sender, RoutedEventArgs e) { if (_layerOrder) ChangeLayers(LayerOp.Up); else Move(-1); }
    private void OnMoveDown(object sender, RoutedEventArgs e) { if (_layerOrder) ChangeLayers(LayerOp.Down); else Move(1); }

    /// <summary>一覧をレイヤー順にしているか（ファイル順でなく）。</summary>
    private bool _layerOrder;

    /// <summary>
    /// レイヤー順の並び（計器の番号）: レイヤーの大きい順・同じならファイルの後ろから。[This] はいちばん下。
    /// グループは崩さず、ひとかたまりにして、いちばん手前の計器の位置に置く（中もレイヤー順）。
    /// </summary>
    private List<int> LayerOrder()
    {
        var doc = _doc!;
        double L(int i) => PanelValues.Double(doc.Sections[i].Get("Layer"), 0);
        var idx = Enumerable.Range(0, doc.Sections.Count).Where(i => doc.Sections[i].Kind != SectionKind.This).ToList();
        // かたまり: グループごと（同じ名前でもファイルで離れていれば別のかたまり）と、グループに入っていない計器 1 つずつ
        // 入れ子のグループは、中も同じように並べる（depth はグループの深さ）
        List<int> Order(List<int> xs, int depth)
        {
            string? G(int i) => doc.Sections[i].Groups.Count > depth ? doc.Sections[i].Groups[depth] : null;
            var units = new List<List<int>>();
            foreach (var i in xs)
            {
                var g = G(i);
                if (g != null && units.Count > 0 && units[^1][^1] == i - 1 && G(i - 1) == g) units[^1].Add(i);
                else units.Add([i]);
            }
            return units.Select(u => u.Count > 1 || G(u[0]) != null ? Order(u, depth + 1) : u)
                .OrderByDescending(u => L(u[0])).ThenByDescending(u => u[0]).SelectMany(u => u).ToList();
        }
        return [.. Order(idx, 0), .. Enumerable.Range(0, doc.Sections.Count).Where(i => doc.Sections[i].Kind == SectionKind.This)];
    }

    private void OnListOrderButton(object sender, RoutedEventArgs e) => SetListOrder(sender == OrderLayerButton, save: true);

    /// <summary>一覧の並び順を変える（次に起動したときも使う）。</summary>
    public void SetListOrder(bool layer, bool save)
    {
        _layerOrder = layer;
        // 押したほうだけをへこませる（もう一度押しても戻らない）
        OrderFileButton.IsChecked = !layer;
        OrderLayerButton.IsChecked = layer;
        MoveUpButton.ToolTip = layer ? "レイヤーを 1 つ上げる（手前へ。Ctrl+]）" : "ファイル内で前へ（同じレイヤーなら奥へ）";
        MoveDownButton.ToolTip = layer ? "レイヤーを 1 つ下げる（奥へ。Ctrl+[）" : "ファイル内で後ろへ（同じレイヤーなら手前へ）";
        if (save) SaveViewSettings();
        if (_doc != null) RebuildAll();
    }

    private void CanUndo(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = Editor.Document.UndoStack.CanUndo;
    private void CanRedo(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = Editor.Document.UndoStack.CanRedo;

    private void OnUndo(object sender, ExecutedRoutedEventArgs e)
    {
        Editor.Undo();
        FlushReparse();
    }

    private void OnRedo(object sender, ExecutedRoutedEventArgs e)
    {
        Editor.Redo();
        FlushReparse();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        bool inText = Keyboard.FocusedElement is TextBox or ComboBox || Editor.TextArea.IsKeyboardFocusWithin;
        if (e.Key == Key.F5) { OnReloadImages(null, null); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.D0 or Key.NumPad0: Canvas.FitAll(); e.Handled = true; return;
                case Key.D1 or Key.NumPad1: Canvas.ZoomCenter(1); e.Handled = true; return;
                case Key.D when !inText: OnDuplicate(null, null); e.Handled = true; return;
                case Key.L: SetLocked(!Canvas.PositionLocked); e.Handled = true; return;
                case Key.D9 or Key.NumPad9: TogglePathNames(); e.Handled = true; return; // テスト用の裏機能
                case Key.A when !inText: SelectAllElements(null, null); e.Handled = true; return;
                case Key.H when !inText: ToggleHiddenOrMembership(); e.Handled = true; return;
                case Key.G when Keyboard.FocusedElement is not TextBox: OnMakeGroup(null, null); e.Handled = true; return;
                case Key.OemCloseBrackets when !inText: ChangeLayers(LayerOp.Up); e.Handled = true; return;
                case Key.OemOpenBrackets when !inText: ChangeLayers(LayerOp.Down); e.Handled = true; return;
            }
        }
        // Ctrl+Shift+H: BVE でも非表示（コメントアウト）・戻す
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.H && !inText) { ToggleDisabled(HiddenTargets()); e.Handled = true; return; }
        // Ctrl+Shift+] / [ : 最前面・最背面
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key is Key.OemCloseBrackets or Key.OemOpenBrackets && Keyboard.FocusedElement is not TextBox)
        {
            ChangeLayers(e.Key == Key.OemCloseBrackets ? LayerOp.Front : LayerOp.Back);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.G && Keyboard.FocusedElement is not TextBox)
        {
            OnUngroup(null, null);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete && !inText && Keyboard.Modifiers == ModifierKeys.None) { OnDelete(null, null); e.Handled = true; }
    }

    // ================= 値のプレビュー（Ctrl＋上下ドラッグ） =================

    private string? _valueKey;
    private double _valueStart;

    /// <summary>計器の状態量のキー（kmphd1 → kmph）。動かせる値がなければ null。</summary>
    private string? StateKeyOf(int section) =>
        _doc != null && section >= 0 && section < _doc.Sections.Count && _doc.Sections[section].Kind != SectionKind.This
            ? Subjects.BaseKey(_doc.Sections[section].Get("Subject") ?? "") : null;

    /// <summary>動作プレビュー欄で、選んだ計器の値の行に色を付ける。</summary>
    private void UpdateStateHighlight()
    {
        if (_doc != null && (_selSet.Count > 1 || _selGroup != null))
        {
            var keys = _selSet.Select(StateKeyOf).OfType<string>().Distinct().ToList();
            var label = _selGroup != null ? $"グループ「{_selGroup}」" : $"{_selSet.Count} 個の計器";
            _states.Highlight(keys, label);
            SelectedStateText.Text = keys.Count == 0
                ? $"選択中: {label}\n動かせる値はありません。"
                : $"選択中: {label}（{_selSet.Count} 個）\n使っている値（{string.Join("、", keys)}）の行に色を付けています。";
            return;
        }
        if (_doc == null || _selected < 0 || _selected >= _doc.Sections.Count)
        {
            _states.Highlight([], "");
            SelectedStateText.Text = "計器を選ぶと、その値の行に色が付きます。パネル上で Ctrl を押しながら計器を上下にドラッグすると値を動かせます。";
            return;
        }
        var s = _doc.Sections[_selected];
        var key = StateKeyOf(_selected);
        var name = DisplayName(s);
        _states.Highlight(key == null ? [] : [key], name);
        SelectedStateText.Text = s.Kind == SectionKind.This
            ? "パネル本体には動かせる値がありません。計器を選んでください。"
            : key == null
                ? $"選択中: {name}\nSubject が true（常表示）か未設定なので、動かせる値はありません。"
                : $"選択中: {name}\n「{key}」の行に色を付けています。パネル上で Ctrl を押しながら上下にドラッグしても動かせます（Shift で細かく、表示灯は Ctrl＋クリックで切り替え）。";
    }

    private void OnValueDragStarted(int section)
    {
        _valueKey = StateKeyOf(section);
        if (_valueKey == null || _states.Range(_valueKey) == null)
        {
            _valueKey = null;
            Message("この計器には動かせる値がありません（Subject が true か未設定）");
            return;
        }
        _valueStart = Canvas.State.GetValueOrDefault(_valueKey);
        SweepCheck.IsChecked = false;
        if (_doc!.Sections[section].Kind == SectionKind.PilotLamp && AllLampsCheck.IsChecked == true)
        {
            // 全点灯のままだと点灯・消灯が見えないので外す
            AllLampsCheck.IsChecked = false;
            OnPreviewOption(this, new RoutedEventArgs());
        }
        ShowValueMessage(_valueKey);
    }

    private void OnValueDragged(int section, double up, bool fine)
    {
        if (_valueKey == null || _states.Range(_valueKey) is not { } r) return;
        double v;
        if (r.IsBool) v = up > 8 ? 1 : up < -8 ? 0 : _valueStart;
        else
        {
            v = _valueStart + up * (r.Max - r.Min) / (fine ? 2000.0 : 200.0);
            v = Math.Clamp(v, r.Min, r.Max);
            if (r.IsInteger) v = Math.Round(v);
        }
        _states.SetValue(_valueKey, v);
        ShowValueMessage(_valueKey);
    }

    private void OnValueClicked(int section)
    {
        if (_valueKey == null || _states.Range(_valueKey) is not { } r) return;
        if (r.IsBool)
        {
            _states.SetValue(_valueKey, Canvas.State.GetValueOrDefault(_valueKey) != 0 ? 0 : 1);
            ShowValueMessage(_valueKey);
        }
        else Message($"{_valueKey}: Ctrl を押したまま上下にドラッグすると値が変わります（上で増える、Shift で細かく）");
    }

    private void ShowValueMessage(string key)
    {
        var v = Canvas.State.GetValueOrDefault(key);
        Message($"{key}（{Subjects.Info(key).Label}）= {PanelValues.Num(Math.Round(v, 2))}　上へドラッグで増える、Shift で細かく");
    }

    // ================= 位置ロック =================

    /// <summary>位置ロックを切り替える。ファイルを開くたびにロックに戻す。</summary>
    private void SetLocked(bool locked)
    {
        if (BlockUnlockInVariationMode(locked)) return;
        Canvas.PositionLocked = locked;
        LockButton.IsChecked = locked;
        LockMenu.IsChecked = locked;
        _props.PositionLocked = locked;
        Message(locked ? "位置をロックしました" : "位置のロックを解除しました。ドラッグ・矢印キーで動かせます");
    }

    private void OnLockButton(object sender, RoutedEventArgs e) => SetLocked(LockButton.IsChecked == true);
    private void OnLockMenu(object sender, RoutedEventArgs e) => SetLocked(LockMenu.IsChecked);

    private void FlashLockButton()
    {
        var anim = new System.Windows.Media.Animation.DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(180)) { RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(3) };
        LockButton.BeginAnimation(OpacityProperty, anim);
    }

    // ================= 表示 =================

    private void OnFit(object sender, RoutedEventArgs e) => Canvas.FitAll();
    private void OnZoom100(object sender, RoutedEventArgs e) => Canvas.ZoomCenter(1);

    private void OnToggleFrame(object sender, RoutedEventArgs e)
    {
        Canvas.ShowViewFrame = ShowFrameMenu.IsChecked;
        Canvas.InvalidateVisual();
    }

    private void OnToggleText(object sender, RoutedEventArgs e)
    {
        if (ShowTextMenu.IsChecked)
        {
            TextRow.Height = _textHeight;
            TextSplitterRow.Height = new GridLength(5);
            Editor.Visibility = Visibility.Visible;
            TextSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            _textHeight = TextRow.Height;
            TextRow.Height = new GridLength(0);
            TextSplitterRow.Height = new GridLength(0);
            Editor.Visibility = Visibility.Collapsed;
            TextSplitter.Visibility = Visibility.Collapsed;
        }
    }

    private void OnPreviewOption(object sender, RoutedEventArgs e)
    {
        Canvas.AllLampsOn = AllLampsCheck.IsChecked == true;
        Canvas.InvalidateVisual();
    }

    private void OnNightChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        Canvas.NightAmount = e.NewValue;
        Canvas.InvalidateVisual();
    }

    private void OnResetState(object sender, RoutedEventArgs e)
    {
        SweepCheck.IsChecked = false;
        _states.Reset();
    }

    private void OnMaxState(object sender, RoutedEventArgs e)
    {
        SweepCheck.IsChecked = false;
        _states.SetAll(r => r.Max);
    }

    // ================= 確認用 =================

    /// <summary>確認用: 番号の計器を選ぶ（グループの計器ならグループごと）。</summary>
    public void SelectForStartup(int index)
    {
        if (_doc != null && index >= 0 && index < _doc.Sections.Count && _doc.Sections[index].TopGroup is { } g)
            ApplySelection(index, _doc.Members(g), g, SelectionSource.Other);
        else SelectSection(index);
    }

    /// <summary>"x,y,zoom"（確認用）。</summary>
    public string? StartupView { get; set; }
    /// <summary>"kmph=60;bc=300;lamps=0;tab=1"（確認用）。</summary>
    public string? StartupState { get; set; }

    public void ApplyStartupOptionsPublic()
    {
        if (StartupView?.Split(',') is { Length: 3 } v && PanelValues.TryDouble(v[2], out var z)
            && PanelValues.TryDouble(v[0], out var x) && PanelValues.TryDouble(v[1], out var y))
        {
            Canvas.ZoomCenter(z);
            Canvas.CenterOn(new Point(x, y));
        }
        if (StartupState == null) return;
        foreach (var kv in StartupState.Split(';'))
        {
            var p = kv.Split('=');
            if (p.Length == 2 && p[0] == "aspect") { SetAspects(p[1], save: false); continue; } // 設定ファイルには保存しない（"16:9,4:3" のように並べられる）
            if (p.Length == 2 && p[0] == "selectall") { SelectAllElements(null, null); continue; }
            if (p.Length != 2 || !PanelValues.TryDouble(p[1], out var val)) continue;
            if (p[0] == "bleed") { SetBleed(val, save: false); continue; }
            if (p[0] == "lamps") { AllLampsCheck.IsChecked = val != 0; OnPreviewOption(this, new RoutedEventArgs()); }
            else if (p[0] == "tab") RightTabs.SelectedIndex = (int)val;
            else if (p[0] == "night") NightSlider.Value = val;
            else _states.SetAll(r => r.Key == p[0] ? val : Canvas.State.GetValueOrDefault(r.Key));
        }
    }

    /// <summary>少し待ってから画面を PNG に保存して閉じる（動作確認用）。</summary>
    public void SnapshotAndClose(string pngPath)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var root = (FrameworkElement)Content;
            var dpi = VisualTreeHelper.GetDpi(this);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            rtb.Render(root);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using (var fs = File.Create(pngPath)) enc.Save(fs);
            if (_scene != null)
                File.WriteAllLines(Path.ChangeExtension(pngPath, ".txt"), _scene.Items.Select(it =>
                    $"#{it.Section} {it.Kind} {it.Subject} L{it.Layer} loc={it.Location} day={it.Day?.PixelWidth}x{it.Day?.PixelHeight} night={it.Night?.PixelWidth}x{it.Night?.PixelHeight} " +
                    $"angle={it.Angle:0.0} ready={it.AngleReady} target={it.TargetAngle(Subjects.Resolve(it.Subject, Canvas.State)):0.0} scale={it.Scale:0.###} value={Subjects.Resolve(it.Subject, Canvas.State)}"));
            _savedText = Editor.Text; // 確認用なので保存の確認は出さない
            Close();
        };
        timer.Start();
    }

    // ================= 表示の補助 =================

    private void UpdateTitle()
    {
        var name = _path != null ? Path.GetFileName(_path) : "（無題）";
        Title = _doc == null ? AppName : $"{(IsDirty ? "● " : "")}{name} - {AppName}" + (_path != null && !PathDisplay.NamesOnly ? $"  [{Path.GetDirectoryName(_path)}]" : "");
    }

    /// <summary>テスト用の裏機能（Ctrl+9）: 画面に出すパスをファイル名だけにする・戻す。覚えておかない。</summary>
    private void TogglePathNames()
    {
        PathDisplay.NamesOnly = !PathDisplay.NamesOnly;
        UpdateTitle();
        BuildRecentMenu();
        _props.Invalidate();
        ShowSelectionDetails(scroll: false);
        Message(PathDisplay.NamesOnly
            ? "（テスト用）パスをファイル名だけで表示しています。右の欄の画像の欄は、その間は書き換えられません。Ctrl+9 で戻します"
            : "（テスト用）パスの表示を元に戻しました");
    }

    private void Message(string text) => MessageText.Text = text;

    private void Warn(string text) => MessageBox.Show(this, text, AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
}

/// <summary>行などの上に入力欄を重ねて出す（ウィンドウの中に描くので、日本語入力の変換の枠が入力欄の所に出る）。</summary>
internal sealed class EditBoxAdorner : System.Windows.Documents.Adorner
{
    private readonly TextBox _box;
    private readonly VisualCollection _visuals;
    private readonly double _x, _y;

    public EditBoxAdorner(UIElement adorned, TextBox box, double x, double y) : base(adorned)
    {
        _box = box;
        _x = x; _y = y;
        _visuals = new VisualCollection(this) { box };
    }

    protected override int VisualChildrenCount => _visuals.Count;
    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override Size MeasureOverride(Size constraint)
    {
        _box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return AdornedElement.RenderSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _box.Arrange(new Rect(new Point(_x, _y), _box.DesiredSize));
        return finalSize;
    }
}
