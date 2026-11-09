using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using BvePanelEditor.Core;
using BvePanelEditor.Core.Psd;
using BvePanelEditor.Views;
using Microsoft.Win32;

namespace BvePanelEditor;

/// <summary>PSD からの読み込みと更新。</summary>
public partial class MainWindow
{
    /// <summary>直近の読み込み・更新の注意書き（通し確認用）。</summary>
    private List<string> _lastPsdWarnings = [];

    private string? PanelDir => _path != null ? Path.GetDirectoryName(_path) : null;

    /// <summary>覚えている PSD のフルパス（なければ null）。</summary>
    private string? LinkedPsd(bool night, int source = 1)
    {
        var src = _doc?.GetPsdSource(source);
        var rel = night ? src?.Night : src?.Day;
        if (string.IsNullOrEmpty(rel) || PanelDir == null) return null;
        var full = Path.GetFullPath(Path.Combine(PanelDir, rel));
        return File.Exists(full) ? full : null;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);


    /// <summary>読み込み画面を開く。パネルを開いていなければ、新しいパネルの保存先を先に聞く。</summary>
    public void StartPsdImport(string? psdPath)
    {
        FlushReparse();
        string? newPanelPath = null;
        if (_doc == null || _path == null)
        {
            if (_doc != null && !ConfirmDiscard()) return;
            var save = new SaveFileDialog
            {
                Title = "新しいパネルファイルの保存先（PSD の画像はこのフォルダの psd に書き出します）",
                Filter = PanelFilters.Save, FileName = "panel.txt",
                InitialDirectory = psdPath != null ? Path.GetDirectoryName(psdPath) : null,
            };
            if (save.ShowDialog(this) != true) return;
            newPanelPath = save.FileName;
        }
        var panelDir = Path.GetDirectoryName(newPanelPath ?? _path)!;
        int layer = _doc != null && _scene != null ? Math.Max(1, _scene.Items.Select(i => i.Layer).DefaultIfEmpty(1).Max()) : 1;
        var w = new PsdImportWindow(this, panelDir, psdPath ?? LinkedPsd(false), psdPath == null ? LinkedPsd(true) : null, layer);
        if (w.ShowDialog() != true || w.Spec == null) return;
        var spec = w.Spec;
        try { ApplyPsdImport(spec, newPanelPath, interactive: true); }
        finally { spec.Day.Dispose(); spec.Night?.Dispose(); }
    }

    /// <summary>読み込みの指定から PNG を書き出し、パネルに計器を足す。</summary>
    private bool ApplyPsdImport(PsdImportSpec spec, string? newPanelPath, bool interactive)
    {
        PsdImportResult result;
        Mouse.OverrideCursor = Cursors.Wait;
        try { result = PsdImport.Build(spec); }
        finally { Mouse.OverrideCursor = null; }
        _lastPsdWarnings = result.Warnings;

        var exist = result.Files.Where(f => File.Exists(f.FullPath)).ToList();
        if (exist.Count > 0 && !Confirm($"次の {exist.Count} 個の画像はすでにあります。上書きしますか？\n\n"
                + string.Join("\n", exist.Take(10).Select(f => "・" + Path.GetFileName(f.FullPath))) + (exist.Count > 10 ? $"\n…ほか {exist.Count - 10} 個" : "")))
            return false;

        // 今のパネルのベース画像（メインパネルがあればその画像）を置き換えるか
        var oldMain = newPanelPath == null ? _doc?.MainPanel : null;
        if (newPanelPath == null && result.Base != null && (oldMain != null ? oldMain.Get("DaytimeImage") : _doc?.This?.Get("DaytimeImage")) is { Length: > 0 } cur
            && !string.Equals(cur.Trim(), result.Base.DayPath, StringComparison.OrdinalIgnoreCase)
            && !Confirm($"パネル本体の画像を「{cur}」から「{result.Base.DayPath}」に置き換えますか？\n（いいえ: ベース画像は書き出さず、表示灯などだけ足します）"))
        {
            var drop = new[] { result.Base.DayPath, result.Base.NightPath }.Where(p => p != null).Select(p => Path.GetFullPath(Path.Combine(spec.PanelDir, p!))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            result.Files.RemoveAll(f => drop.Contains(f.FullPath));
            result.Base = null;
        }

        try
        {
            foreach (var f in result.Files) WriteNewFile(f.FullPath, f.Png);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"画像を書き出せませんでした:\n{ex.Message}");
            return false;
        }

        // 新しいパネルでベースがあれば、[This] には画像を書かず、ベースはメインパネル（常表示の表示灯）にする
        PanelSection? baseTarget = null;
        if (newPanelPath != null && result.Base != null)
        {
            _doc = MainPanel.CreateNew(result.Base.DayPath, result.Base.NightPath, result.Base.Width, result.Base.Height);
            _path = Path.GetFullPath(newPanelPath);
            _info = TextFileInfo.NewUtf8();
            _vehicle = null;
            _selected = -1;
            _selSet = [];
            _selGroup = null;
            _folded.Clear();
            SetEditorText(_doc.Serialize(), clearUndo: true);
            _savedText = "";
            baseTarget = _doc.MainPanel;
            baseTarget!.PsdLayers.AddRange(result.Base.Layers);
        }
        else if (newPanelPath != null)
        {
            _doc = PanelDocument.CreateNew(result.Base?.DayPath ?? "", result.Base?.Width ?? spec.Day.Width, result.Base?.Height ?? spec.Day.Height);
            _path = Path.GetFullPath(newPanelPath);
            _info = TextFileInfo.NewUtf8();
            _vehicle = null;
            _selected = -1;
            _selSet = [];
            _selGroup = null;
            _folded.Clear();
            SetEditorText(_doc.Serialize(), clearUndo: true);
            _savedText = "";
        }
        var doc = _doc!;
        var self = doc.This;
        if (self == null)
        {
            self = PanelDocument.CreateNew("", spec.Day.Width, spec.Day.Height).Sections[0];
            doc.Sections.Insert(0, self);
        }
        if (result.Base != null && baseTarget == null && oldMain != null)
        {
            // 今のメインパネルの画像を置き換える
            oldMain.Set("DaytimeImage", result.Base.DayPath);
            oldMain.Set("NighttimeImage", result.Base.NightPath);
            oldMain.PsdLayers.Clear();
            oldMain.PsdLayers.AddRange(result.Base.Layers);
            baseTarget = oldMain;
        }
        else if (result.Base != null && baseTarget == null)
        {
            baseTarget = self;
            self.Set("DaytimeImage", result.Base.DayPath);
            self.Set("NighttimeImage", result.Base.NightPath);
            self.PsdLayers.Clear();
            self.PsdLayers.AddRange(result.Base.Layers);
        }

        // 名前が重なるグループは番号を付けて分ける
        foreach (var g in result.Sections.SelectMany(s => s.Groups).Distinct().ToList())
        {
            if (!doc.GroupNames().Contains(g)) continue;
            var n = doc.UniqueGroupName(g);
            foreach (var s in result.Sections)
                for (int k = 0; k < s.Groups.Count; k++) if (s.Groups[k] == g) s.Groups[k] = n;
        }
        // どの PSD から作ったかを覚える。今までと同じ昼の PSD ならその番号、別の PSD なら新しい番号（前の計器のつながりは変えない）
        var source = doc.PsdSources.FirstOrDefault(src => src.Day.Length > 0 && SamePath(Path.Combine(spec.PanelDir, src.Day), spec.Day.FilePath));
        if (source == null)
        {
            source = doc.GetOrAddPsdSource(doc.PsdSources.Count == 0 ? 1 : doc.PsdSources.Max(x => x.Id) + 1);
            source.Day = PsdImport.PathFrom(spec.PanelDir, spec.Day.FilePath);
        }
        if (spec.Night != null) source.Night = PsdImport.PathFrom(spec.PanelDir, spec.Night.FilePath);
        foreach (var sec in result.Sections) sec.PsdSource = source.Id;
        if (baseTarget != null) baseTarget.PsdSource = source.Id;
        int start = doc.Sections.Count;
        doc.Sections.AddRange(result.Sections);
        // 新しいパネルのメインパネル（レイヤー 1）より、読み込んだ計器を上にする
        if (newPanelPath != null && doc.MainPanel is { } nm) MainPanel.RaiseAbove(doc, MainPanel.Layer, nm);

        CommitModel();
        _cache.Clear();
        RebuildAll();
        if (newPanelPath != null)
        {
            SetLocked(true);
            Dispatcher.BeginInvoke(() => Canvas.FitAll(), DispatcherPriority.Loaded);
        }
        var added = Enumerable.Range(start, result.Sections.Count).ToList();
        if (added.Count > 0) ApplySelection(added[0], added, null, SelectionSource.Other);

        int lamps = result.Sections.Count(s => s.Kind == SectionKind.PilotLamp);
        int nums = result.Sections.Count(s => s.Kind == SectionKind.DigitalNumber);
        int noSubject = result.Sections.Count(s => string.IsNullOrEmpty(s.Get("Subject")));
        Message($"PSD から読み込みました（{(result.Base != null ? "ベース・" : "")}表示灯 {lamps} 個・数字 {nums} 個、画像 {result.Files.Count} 枚）"
            + (noSubject > 0 ? $"。Subject が空の計器 {noSubject} 個は右の欄で設定してください" : "")
            + (newPanelPath != null ? "。パネルファイルはまだ保存していません（Ctrl+S）" : ""));
        if (interactive && result.Warnings.Count > 0)
            MessageBox.Show(this, "読み込みましたが、次の点に注意してください。\n\n" + string.Join("\n", result.Warnings.Distinct().Take(15).Select(w => "・" + w))
                + (result.Warnings.Count > 15 ? $"\n…ほか {result.Warnings.Count - 15} 件" : ""), "PSD から読み込み", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

}
