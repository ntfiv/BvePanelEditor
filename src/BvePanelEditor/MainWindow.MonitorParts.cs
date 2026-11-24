using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BvePanelEditor.Core;
using BvePanelEditor.Core.Imaging;
using BvePanelEditor.Core.Psd;
using BvePanelEditor.Rendering;
using BvePanelEditor.Views;

namespace BvePanelEditor;

/// <summary>
/// モニタくんに合わせた読み込み: SVG を PNG にして読み込む、画像を 4 隅で変形して貼りこむ、
/// パネルファイルの一部（モニタ・スタフなど）をグループとして取り込む。モニタくんの作業ファイル（.monitakun）は、まだ知らせるだけ。
/// </summary>
public partial class MainWindow
{
    /// <summary>モニタくんの作業ファイルの拡張子。</summary>
    public const string MonitaKunExtension = ".monitakun";

    public static bool IsMonitaKunFile(string path) => Path.GetExtension(path).Equals(MonitaKunExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>モニタくんの作業ファイルは、まだ読めないことを知らせる。</summary>
    private void NoticeMonitaKunFile(string path) =>
        Warn($"「{Path.GetFileName(path)}」はモニタくんの作業ファイルです。パネルくんではまだ読み込めません。\n" +
             "モニタくんで BVE 用に書き出したパネルファイルを、ファイル → パネルファイルの一部を取り込む で読み込んでください。");

    /// <summary>画像を置く前に、パネルの保存先を決める（画像はパネルのフォルダから見た場所で書くため）。やめたら false。</summary>
    private bool EnsureSavedPanelForImages()
    {
        EnsurePanel();
        if (_path != null) return true;
        var save = new Microsoft.Win32.SaveFileDialog
        {
            Title = "パネルファイルの保存先（画像はこのフォルダから見た場所で書きます）",
            Filter = PanelFilters.Save, FileName = "panel.txt",
        };
        if (save.ShowDialog(this) != true) return false;
        _path = save.FileName;
        UpdateTitle();
        return true;
    }

    // ================= SVG =================

    /// <summary>確認用: SVG の倍率を聞く画面の代わりに使う答え（null なら画面で聞く）。</summary>
    public static Func<string, string?>? SvgScaleAnswer;

    /// <summary>
    /// files の中の SVG を PNG にする（倍率を 1 度だけ聞き、パネルのフォルダの svg フォルダに書く）。
    /// SVG 以外はそのまま。やめた・読めなかったら null。
    /// </summary>
    private List<string>? ConvertSvgs(IReadOnlyList<string> files)
    {
        if (!files.Any(SvgImage.IsSvg)) return files.ToList();
        if (PanelDir == null) { Warn("先にパネルファイルを保存してください（画像をパネルからの場所で指すため）。"); return null; }
        var loaded = new Dictionary<string, SvgImage.Loaded>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files.Where(SvgImage.IsSvg))
        {
            try { loaded[f] = SvgImage.Load(f); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Warn($"「{Path.GetFileName(f)}」を読めませんでした。\n{ex.Message}");
                return null;
            }
        }
        var first = loaded.First();
        if (AskSvgScale(first.Key, first.Value, loaded.Count, 1) is not { } scale) return null;
        var result = new List<string>();
        try
        {
            foreach (var f in files)
            {
                if (!loaded.TryGetValue(f, out var svg)) { result.Add(f); continue; }
                var px = SvgImage.Rasterize(svg, scale);
                var (rel, _) = WritePanelPng("svg", Path.GetFileNameWithoutExtension(f), px, null, pow2: false);
                result.Add(Path.GetFullPath(Path.Combine(PanelDir, rel)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"PNG を書き出せませんでした:\n{ex.Message}");
            return null;
        }
        return result;
    }

    /// <summary>SVG を PNG にする倍率を聞く（count は一緒に PNG にする数）。やめた・おかしな値なら null。</summary>
    private double? AskSvgScale(string file, SvgImage.Loaded svg, int count, double initial)
    {
        string size = $"{Math.Round(svg.Width)}×{Math.Round(svg.Height)}";
        string msg = $"「{Path.GetFileName(file)}」{(count > 1 ? $" ほか {count - 1} 個" : "")}を PNG にします。\n" +
                     $"SVG に書いてある大きさは {size} です。倍率を入力してください（例: 1、2、0.5）。";
        string? ans = SvgScaleAnswer != null ? SvgScaleAnswer(msg) : InputDialog.Show(this, "SVG の大きさ", msg, PanelValues.Num(initial));
        if (ans == null) return null;
        if (!PanelValues.TryDouble(ans.Trim().TrimEnd('倍', 'x', 'X', '×'), out var scale) || scale <= 0 || scale > 16)
        {
            Warn("倍率は 0 より大きく 16 以下の数で入力してください。");
            return null;
        }
        return scale;
    }

    // ================= パネルファイルの一部を取り込む =================

    /// <summary>ファイル → パネルファイルの一部を取り込む: モニタ・スタフなどの部分だけのパネルファイルを選んで、1 つのグループとして足す。</summary>
    private void OnImportFragment(object? sender, RoutedEventArgs? e)
    {
        if (!EnsureSavedPanelForImages()) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { AddToRecent = false, Title = "取り込むパネルファイル（モニタ・スタフなどの部分）", Filter = PanelFilters.Open };
        if (PanelDir != null) dlg.InitialDirectory = PanelDir;
        if (dlg.ShowDialog(this) != true) return;
        ImportFragment(dlg.FileName);
    }

    /// <summary>
    /// パネルファイルの一部を、今のパネルの最後に 1 つのグループとして足す。[This] があれば取り込まない。
    /// 画像はパネルのフォルダに写し（元のファイルからの場所のフォルダ分けは保つ）、パネルからの場所に書き直す。
    /// </summary>
    public void ImportFragment(string file)
    {
        if (_doc == null || PanelDir == null) return;
        FlushReparse();
        PanelDocument frag;
        try { frag = PanelFragment.Parse(BveText.Read(file).Text); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"開けませんでした:\n{ex.Message}");
            return;
        }
        if (PanelFragment.HasThis(frag))
        {
            Warn($"「{Path.GetFileName(file)}」には [This] があるので取り込めません。\nモニタやスタフなど、パネルの一部だけを書いたパネルファイルを選んでください。");
            return;
        }
        if (frag.Sections.Count == 0) { Warn($"「{Path.GetFileName(file)}」にオブジェクトがありません。"); return; }
        if (Path.GetFullPath(file).Equals(Path.GetFullPath(_path!), StringComparison.OrdinalIgnoreCase)) { Warn("今開いているパネルファイルは取り込めません。"); return; }

        var prepared = PanelFragment.Prepare(_doc, frag, Path.GetFileNameWithoutExtension(file));
        var fragDir = Path.GetDirectoryName(Path.GetFullPath(file))!;
        var copied = new List<string>();
        var missing = new List<string>();
        try
        {
            foreach (var s in prepared.Sections)
            {
                // 変形の元の画像は動かさず、パネルから見た場所に直す
                PanelDocument.RebaseWarp(s, fragDir, PanelDir);
                foreach (var (line, v) in PanelFragment.ImageValues(s).ToList())
                {
                    var full = Path.GetFullPath(Path.IsPathRooted(v) ? v : Path.Combine(fragDir, v.Replace('/', '\\')));
                    if (!File.Exists(full))
                    {
                        missing.Add(v);
                        if (!Path.IsPathRooted(v)) line.SetValue(PanelDocument.RebasePath(v, fragDir, PanelDir));
                        continue;
                    }
                    // 元のファイルから見たフォルダ（「monitor\」など）は、パネルのフォルダの中でも同じにする（外を指していればパネルのフォルダの直下）
                    var relDir = Path.IsPathRooted(v) ? "" : Path.GetDirectoryName(v.Replace('/', '\\')) ?? "";
                    if (relDir.Split('\\').Contains("..")) relDir = "";
                    var rel = CopyIntoPanel(full, Path.Combine(PanelDir, relDir), copied);
                    line.SetValue(rel);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"画像をパネルのフォルダにコピーできませんでした:\n{ex.Message}");
            return;
        }

        int first = _doc.Sections.Count;
        _doc.Sections.AddRange(prepared.Sections);
        var added = Enumerable.Range(first, prepared.Sections.Count).ToList();
        CommitModel();
        _cache.Clear();
        RebuildAll();
        ApplySelection(added[0], added, prepared.Group, SelectionSource.Other);
        Canvas.Focus();
        var notes = new List<string>(prepared.Notes);
        if (copied.Count > 0) notes.Add($"画像 {copied.Count} 枚をパネルのフォルダにコピーしました");
        if (missing.Count > 0) notes.Add($"見つからない画像があります（{string.Join("、", missing.Distinct().Take(3))}{(missing.Distinct().Count() > 3 ? " ほか" : "")}）");
        Message($"「{Path.GetFileName(file)}」のオブジェクト {added.Count} 個を、グループ「{prepared.Group}」として取り込みました。"
            + (notes.Count > 0 ? string.Join("。", notes) + "。" : ""));
    }
}
