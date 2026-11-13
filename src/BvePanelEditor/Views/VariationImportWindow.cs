using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BvePanelEditor.Core;
using Microsoft.Win32;

namespace BvePanelEditor.Views;

/// <summary>
/// 既存のバリエーション別パネルを選んで、マスターパネルを作る画面。
/// パネルごとにバリエーション名を付け、マスターの保存先を決める。出力ファイルは元のパネルファイル（書き出すと元の場所に作り直せる）。
/// </summary>
public sealed partial class VariationImportWindow : Window
{
    private sealed class Entry
    {
        public string Path = "";
        public TextBox Name = new() { Width = 150, Margin = new Thickness(0, 1, 6, 1) };
    }

    private readonly List<Entry> _entries = [];
    private readonly StackPanel _rows = new();
    private readonly TextBox _master = new() { Margin = new Thickness(0, 2, 0, 2) };
    private readonly TextBlock _note = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private bool _masterEdited;

    /// <summary>OK のとき: 取り込むパネル（バリエーション名・パネルファイル）。</summary>
    public List<(string Name, string Path)> Sources { get; } = [];
    /// <summary>確認用: 足したパネルファイル・知らせ。</summary>
    public List<string> FilesForTest => _entries.Select(e => e.Path).ToList();
    public string NoteForTest => _note.Text;

    /// <summary>OK のとき: マスターパネルの保存先。</summary>
    public string MasterPath { get; private set; } = "";

    public VariationImportWindow(Window owner)
    {
        Owner = owner;
        Title = "既存のパネルからマスターを作る";
        Width = 720;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Yu Gothic UI");
        FontSize = 12;
        Background = SystemColors.WindowBrush;

        var root = new DockPanel { Margin = new Thickness(10) };
        var top = new TextBlock
        {
            Text = "バリエーションごとに作ってあるパネルファイルを選ぶと、中身（見出しとキー・値）が同じオブジェクトを 1 つにまとめたマスターパネルを作ります。"
                + "どのバリエーションに入るかは「; @group 名前」で書きます。元のパネルは書き換えません。"
                + "別々のフォルダのパネルでも、画像の場所をマスターからのものに直して取り込みます。出力ファイルは元のパネルファイルにするので、書き出すと元の場所に作り直せます。"
                + "先頭のパネルが「基準パネル」で、マスターの並び・1 行目・コメントとグループは基準パネルのものを使います。"
                + "切り抜き元や PSD とのつながりも基準パネルのものだけが残るので、画像取り込み（切り抜き・PSD）から作ったパネルは先頭に置くと、そのつながりを引き継げます。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        };
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Button { Content = "マスターを作る", Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        ok.Click += (_, _) => OnOk();
        var cancel = new Button { Content = "やめる", Padding = new Thickness(14, 3, 14, 3), IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var bottom = new StackPanel();
        bottom.Children.Add(new TextBlock { Text = "マスターパネルの保存先", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        var masterRow = new DockPanel();
        var pick = new Button { Content = "参照…", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(4, 2, 0, 2) };
        pick.Click += (_, _) => PickMaster();
        DockPanel.SetDock(pick, Dock.Right);
        masterRow.Children.Add(pick);
        _master.TextChanged += (_, _) => { if (_master.IsKeyboardFocusWithin) _masterEdited = true; };
        masterRow.Children.Add(_master);
        bottom.Children.Add(masterRow);
        bottom.Children.Add(_note);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        var listHead = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var add = new Button { Content = "パネルを足す…", Padding = new Thickness(8, 1, 8, 1) };
        add.Click += (_, _) => AddFilesDialog();
        DockPanel.SetDock(add, Dock.Right);
        listHead.Children.Add(add);
        listHead.Children.Add(new TextBlock { Text = "取り込むパネル（バリエーション名 ／ ファイル）。先頭のパネルが基準パネルです", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(listHead, Dock.Top);
        root.Children.Add(listHead);
        root.Children.Add(new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1) });
        Content = root;
    }

    private void AddFilesDialog()
    {
        var dlg = new OpenFileDialog { AddToRecent = false, Multiselect = true, Title = "取り込むパネルファイル", Filter = "パネルファイル|*.txt;*.cfg|すべて|*.*" };
        if (_entries.Count > 0) dlg.InitialDirectory = Path.GetDirectoryName(_entries[^1].Path);
        if (dlg.ShowDialog(this) == true) AddFiles(dlg.FileNames);
    }

    /// <summary>
    /// パネルファイルかどうか（1 行目が「BveTs Instrument Panel」、または古い書式で [This] があるもの）。違えば、何のファイルかを返す。
    /// </summary>
    public static string? NotPanelReason(string path)
    {
        try
        {
            // 先頭（64KB）だけを読む（大きな画像などを全部読まない）
            var head = new byte[64 * 1024];
            int n;
            using (var fs = File.OpenRead(path)) n = fs.Read(head, 0, head.Length);
            var (text, _) = BveText.Decode(head.AsSpan(0, n).ToArray());
            return BveFiles.Detect(text) switch
            {
                BveFileKind.Panel => null,
                BveFileKind.Vehicle => "車両ファイル",
                BveFileKind.Scenario => "シナリオファイル",
                _ => text.Contains('\0') ? "テキストでないファイル" : "パネルファイルではない",
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "読めない";
        }
    }

    /// <summary>
    /// パネルファイルを足す（同じファイルは足さない。パネルファイルでないものは足さずに知らせる）。
    /// 名前の初期値はファイル名（同じ名前が並ぶならフォルダ名）。
    /// </summary>
    public void AddFiles(IEnumerable<string> files)
    {
        var rejected = new List<string>();
        foreach (var f in files.Select(Path.GetFullPath))
        {
            if (_entries.Any(e => string.Equals(e.Path, f, StringComparison.OrdinalIgnoreCase))) continue;
            if (NotPanelReason(f) is { } why) { rejected.Add($"{Path.GetFileName(f)}（{why}）"); continue; }
            _entries.Add(new Entry { Path = f });
        }
        _note.Text = rejected.Count > 0 ? $"パネルファイルではないので外しました: {string.Join("、", rejected)}" : "";
        // 名前の初期値（まだ空のものだけ）
        bool sameFileNames = _entries.GroupBy(e => Path.GetFileNameWithoutExtension(e.Path), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1);
        var used = _entries.Where(e => e.Name.Text.Length > 0).Select(e => e.Name.Text).ToHashSet();
        foreach (var e in _entries.Where(e => e.Name.Text.Length == 0))
        {
            var raw = sameFileNames ? Path.GetFileName(Path.GetDirectoryName(e.Path)!) : Path.GetFileNameWithoutExtension(e.Path);
            var n = BadChars().Replace(raw, "_");
            if (n.Length == 0 || !VariationCondition.IsValidName(n)) n = "バリエーション";
            var u = n;
            for (int k = 2; used.Contains(u); k++) u = $"{n}_{k}";
            used.Add(u);
            e.Name.Text = u;
        }
        if (!_masterEdited && _entries.Count > 0)
        {
            var first = _entries[0].Path;
            _master.Text = Path.Combine(Path.GetDirectoryName(first)!, $"_{Path.GetFileNameWithoutExtension(first)}_master{Path.GetExtension(first)}");
        }
        RefreshRows();
    }

    [GeneratedRegex(@"[\s,!;#\[\]=、]")]
    private static partial Regex BadChars();

    private void RefreshRows()
    {
        _rows.Children.Clear();
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            var row = new DockPanel { Margin = new Thickness(4, 1, 4, 1) };
            var remove = new Button { Content = "外す", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(6, 1, 0, 1) };
            remove.Click += (_, _) => { _entries.Remove(e); RefreshRows(); };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            if (i > 0)
            {
                var toBase = new Button { Content = "基準にする", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(6, 1, 0, 1), ToolTip = "このパネルを基準パネル（先頭）にする" };
                toBase.Click += (_, _) =>
                {
                    _entries.Remove(e);
                    _entries.Insert(0, e);
                    if (!_masterEdited) // 保存先の初期値は基準パネルから付ける
                        _master.Text = Path.Combine(Path.GetDirectoryName(e.Path)!, $"_{Path.GetFileNameWithoutExtension(e.Path)}_master{Path.GetExtension(e.Path)}");
                    RefreshRows();
                };
                DockPanel.SetDock(toBase, Dock.Right);
                row.Children.Add(toBase);
            }
            (e.Name.Parent as Panel)?.Children.Remove(e.Name);
            DockPanel.SetDock(e.Name, Dock.Left);
            row.Children.Add(e.Name);
            row.Children.Add(new TextBlock { Text = (i == 0 ? "（基準）" : "") + e.Path, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = i == 0 ? "基準パネル: 並び・1 行目・コメントとグループ、切り抜き元や PSD とのつながりは、このパネルのものを使います\n" + e.Path : e.Path });
            _rows.Children.Add(row);
        }
        if (_entries.Count == 0) _rows.Children.Add(new TextBlock { Text = "「パネルを足す…」で 2 つ以上のパネルファイルを選んでください", Foreground = Brushes.Gray, Margin = new Thickness(6) });
    }

    private void PickMaster()
    {
        var dlg = new SaveFileDialog
        {
            AddToRecent = false, Title = "マスターパネルの保存先", Filter = "パネルファイル|*.txt;*.cfg|すべて|*.*",
            FileName = Path.GetFileName(_master.Text), InitialDirectory = Path.GetDirectoryName(_master.Text) is { } d && Directory.Exists(d) ? d : null,
        };
        if (dlg.ShowDialog(this) != true) return;
        _master.Text = dlg.FileName;
        _masterEdited = true;
    }

    /// <summary>入力を確かめて Sources・MasterPath を決める。問題があれば知らせて false。</summary>
    public bool Validate()
    {
        _note.Text = "";
        if (_entries.Count < 2) { _note.Text = "パネルを 2 つ以上選んでください"; return false; }
        var names = _entries.Select(e => e.Name.Text.Trim()).ToList();
        if (names.FirstOrDefault(n => !VariationCondition.IsValidName(n)) is { } bad) { _note.Text = $"バリエーション名「{bad}」は使えません（空白と , ! ; # [ ] = 、 は使えません）"; return false; }
        if (names.GroupBy(n => n).FirstOrDefault(g => g.Count() > 1) is { } dup) { _note.Text = $"バリエーション名「{dup.Key}」が重なっています"; return false; }
        var master = _master.Text.Trim();
        if (master.Length == 0) { _note.Text = "マスターパネルの保存先を決めてください"; return false; }
        master = Path.GetFullPath(master);
        if (_entries.Any(e => string.Equals(e.Path, master, StringComparison.OrdinalIgnoreCase))) { _note.Text = "マスターパネルは、取り込むパネルと別のファイルにしてください"; return false; }
        if (!Directory.Exists(Path.GetDirectoryName(master))) { _note.Text = "マスターパネルの保存先のフォルダがありません"; return false; }
        Sources.Clear();
        Sources.AddRange(_entries.Select((e, i) => (names[i], e.Path)));
        MasterPath = master;
        return true;
    }

    private void OnOk()
    {
        if (!Validate()) return;
        DialogResult = true;
    }
}
