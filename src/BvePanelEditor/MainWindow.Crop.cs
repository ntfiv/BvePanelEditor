using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using BvePanelEditor.Core;
using BvePanelEditor.Core.Crop;
using BvePanelEditor.Core.Psd;
using BvePanelEditor.Rendering;
using BvePanelEditor.Views;
using Microsoft.Win32;

namespace BvePanelEditor;

/// <summary>4 点の画像からパネルを作る・切り抜き元から更新する。</summary>
public partial class MainWindow
{
    private void OnCropPanel(object? sender, RoutedEventArgs? e) => StartCropPanel(null);

    /// <summary>PSD を開いた・ドロップしたとき、読み込み方を選ぶ。</summary>
    private void OpenPsd(string path)
    {
        int pick = ChoiceDialog.Show(this, "PSD の読み込み方", $"「{Path.GetFileName(path)}」をどう使いますか？",
        [
            "レイヤーごとに読み込む（ベース・表示灯・数字のコマに分ける）",
            "レイヤーを 4 点の画像（昼・夜 × 無発光・発光）に振り分けて、画像からパネルを作る",
        ]);
        if (pick == 0) StartPsdImport(path);
        else if (pick == 1) StartCropPanel(path);
    }

    private static readonly string CropPanelsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BvePanelEditor", "crop_panels.txt");

    /// <summary>画像から作ったパネル（覚えているもの）。</summary>
    private static List<string> LoadCropPanels()
    {
        try { return File.Exists(CropPanelsFile) ? File.ReadAllLines(CropPanelsFile).Where(l => l.Length > 0).ToList() : []; }
        catch (IOException) { return []; }
    }

    /// <summary>画像から作ったパネルを覚えておく（その画像を開いたとき、このパネルを開くか聞くため）。</summary>
    private static void RememberCropPanel(string panelPath)
    {
        if (IsTestRun) return;
        var list = LoadCropPanels();
        list.RemoveAll(p => string.Equals(p, panelPath, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, panelPath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CropPanelsFile)!);
            File.WriteAllLines(CropPanelsFile, list.Take(100));
        }
        catch (IOException) { }
    }

    /// <summary>この画像（PSD）を使っているパネルファイル（今開いているものを除く）。</summary>
    public List<string> FindPanelsForImage(string imagePath) =>
        CropPanelFinder.Find(imagePath, LoadCropPanels())
            .Where(p => _path == null || !string.Equals(p, _path, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// 画像（PSD）を開こうとしたとき、それを使っているパネルがあれば、そのパネルを開くか聞く。
    /// 開いた・やめたなら true、「新しく作る」を選んだなら false（画像からパネルを作るへ進む）、パネルがなければ null。
    /// </summary>
    private bool? OfferExistingPanel(string imagePath)
    {
        List<string> found;
        Mouse.OverrideCursor = Cursors.Wait;
        try { found = FindPanelsForImage(imagePath); }
        finally { Mouse.OverrideCursor = null; }
        if (found.Count == 0) return null;
        var shown = found.Take(5).ToList();
        var items = shown.Select(p => PathDisplay.NamesOnly ? $"パネル「{Path.GetFileName(p)}」を開く" : $"パネル「{Path.GetFileName(p)}」を開く（{Path.GetDirectoryName(p)}）").ToList();
        items.Add("新しく画像からパネルを作る（今あるパネルとは別に作ります）");
        int pick = ChoiceDialog.Show(this, "この画像から作ったパネルがあります",
            $"「{Path.GetFileName(imagePath)}」を使っているパネルがあります。パネルを開きますか？", items);
        if (pick < 0) return true;
        if (pick < shown.Count) { OpenPath(shown[pick]); return true; }
        return false;
    }

    /// <summary>最近使ったファイルなどから画像を開くとき、画像からパネルを作ってよいか確かめる。</summary>
    private bool ConfirmImageOpen(string imagePath, OpenSource source)
    {
        var from = source == OpenSource.Recent ? "最近使ったファイルから" : "起動するときに";
        int pick = ChoiceDialog.Show(this, "画像を開きます",
            $"{from}画像「{Path.GetFileName(imagePath)}」を開こうとしています。\n画像はパネルファイルではないので、開くと「画像からパネルを作る」になります。\n（パネルファイルを開くつもりなら「やめる」を選んで、パネルファイルを開いてください）",
        [
            "この画像から新しくパネルを作る",
            "やめる",
        ]);
        return pick == 0;
    }

    /// <summary>作る画面を開く。パネルを開いていなければ、新しいパネルの保存先を先に聞く。</summary>
    public void StartCropPanel(string? firstImage)
    {
        FlushReparse();
        string? newPanelPath = null;
        if (_doc == null || _path == null)
        {
            if (_doc != null && !ConfirmDiscard()) return;
            var save = new SaveFileDialog
            {
                Title = "新しいパネルファイルの保存先（切り抜いた画像はこのフォルダの crop に書き出します）",
                Filter = PanelFilters.Save, FileName = "panel.txt",
                InitialDirectory = firstImage != null ? Path.GetDirectoryName(firstImage) : null,
                OverwritePrompt = false, // すでにあるときは下で聞く（開くこともできるように）
            };
            if (save.ShowDialog(this) != true) return;
            newPanelPath = save.FileName;
            if (File.Exists(newPanelPath))
            {
                int pick = ChoiceDialog.Show(this, "パネルファイルがすでにあります", $"「{Path.GetFileName(newPanelPath)}」はすでにあります。どうしますか？",
                [
                    "そのパネルを開く（作り直さない）",
                    "新しく作って置き換える（保存したときに上書きします）",
                ]);
                if (pick == 0) { OpenPath(newPanelPath); return; }
                if (pick != 1) return;
            }
        }
        var panelDir = Path.GetDirectoryName(newPanelPath ?? _path)!;
        int layer = _doc != null && _scene != null ? Math.Max(1, _scene.Items.Select(i => i.Layer).DefaultIfEmpty(1).Max()) : 1;
        // 今のパネルのメインパネル（なければ [This]）が画像を指していれば、それを最初の 1 点にする
        if (firstImage == null && (_doc?.MainPanel?.Get("DaytimeImage") ?? (IsBlankThis() ? null : _doc?.This?.Get("DaytimeImage"))) is { Length: > 0 } cur && PanelDir != null)
        {
            var full = Path.GetFullPath(Path.Combine(PanelDir, cur));
            if (File.Exists(full) && !full.Contains($"{Path.DirectorySeparatorChar}crop{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) firstImage = full;
        }
        var w = new ImagePanelWindow(this, panelDir, firstImage, layer);
        if (w.ShowDialog() != true || w.Spec == null) return;
        ApplyCropBuild(w.Spec, newPanelPath, interactive: true);
    }

    /// <summary>切り抜いた PNG を書き出し、パネルを作る（または今のパネルに足す）。</summary>
    private bool ApplyCropBuild(CropBuildSpec spec, string? newPanelPath, bool interactive)
    {
        CropBuildResult result;
        Mouse.OverrideCursor = Cursors.Wait;
        try { result = CropBuild.Build(spec); }
        finally { Mouse.OverrideCursor = null; }

        var exist = result.Files.Where(f => File.Exists(f.FullPath)).ToList();
        if (exist.Count > 0 && !Confirm($"次の {exist.Count} 個の画像はすでにあります。上書きしますか？\n\n"
                + string.Join("\n", exist.Take(10).Select(f => "・" + Path.GetFileName(f.FullPath))) + (exist.Count > 10 ? $"\n…ほか {exist.Count - 10} 個" : "")))
            return false;
        // 今のパネルに足すとき: パネル本体（メインパネルがあればその画像、なければ [This] の画像）を置き換えるか
        var oldMain = newPanelPath == null ? _doc?.MainPanel : null;
        bool replaceBase = true;
        var curBase = oldMain != null ? oldMain.Get("DaytimeImage") : IsBlankThis() ? null : _doc?.This?.Get("DaytimeImage");
        if (newPanelPath == null && curBase is { Length: > 0 } cur && !string.Equals(cur.Trim(), result.BaseDay, StringComparison.OrdinalIgnoreCase))
            replaceBase = Confirm($"{(oldMain != null ? "メインパネル" : "パネル本体")}の画像を「{cur}」から「{result.BaseDay}」に置き換えますか？\n（いいえ: そのままで、表示灯だけ足します）");
        // メインパネルの表示灯は、置き換えないなら足さない。今のメインパネルを置き換えるなら、その表示灯の画像を変える
        bool useNewMain = newPanelPath != null || (replaceBase && oldMain == null) || _doc?.This == null;
        if (!useNewMain && result.Main != null) result.Sections.Remove(result.Main);

        try
        {
            foreach (var f in result.Files) WriteNewFile(f.FullPath, f.Png);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"画像を書き出せませんでした:\n{ex.Message}");
            return false;
        }

        if (newPanelPath != null)
        {
            _doc = PanelDocument.CreateNew(null, spec.Width, spec.Height);
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
            self = PanelDocument.CreateNew(null, spec.Width, spec.Height).Sections[0];
            doc.Sections.Insert(0, self);
        }
        else if (useNewMain)
        {
            // [This] からは画像を外す（前の [This] の PSD・切り抜きのつながりは使わない）
            self.Set("DaytimeImage", null);
            self.Set("NighttimeImage", null);
            self.PsdLayers.Clear();
            doc.CropSets.RemoveAll(c => c.Name == CropSetNames.Base);
        }
        else if (replaceBase && oldMain != null && result.Main != null)
        {
            oldMain.Set("DaytimeImage", result.Main.Get("DaytimeImage"));
            oldMain.Set("NighttimeImage", result.Main.Get("NighttimeImage"));
            oldMain.CropSet = result.Main.CropSet;
        }

        // 切り抜き元の組（同じ名前は新しいもので置き換える）
        foreach (var set in result.Sets)
        {
            doc.CropSets.RemoveAll(c => c.Name == set.Name);
            doc.CropSets.Add(set);
        }
        int start = doc.Sections.Count;
        doc.Sections.AddRange(result.Sections);
        // 今のパネルに新しいメインパネルを足したときは、前からあるオブジェクトをメインパネルより上にする
        if (newPanelPath == null && useNewMain && result.Main != null)
            MainPanel.RaiseAbove(doc, (int)PanelValues.Double(result.Main.Get("Layer"), MainPanel.Layer), [.. result.Sections]);

        CommitModel();
        _cache.Clear();
        RebuildAll();
        if (newPanelPath != null)
        {
            SetLocked(true);
            Dispatcher.BeginInvoke(() => Canvas.FitAll(), DispatcherPriority.Loaded);
        }
        var added = Enumerable.Range(start, result.Sections.Count).Where(i => !doc.Sections[i].IsMainPanel).ToList();
        if (added.Count > 0) ApplySelection(added[0], added, null, SelectionSource.Other);
        int noSubject = result.Sections.Count(s => string.IsNullOrEmpty(s.Get("Subject")));
        Message($"画像からパネルを作りました（表示灯 {result.Sections.Count} 個、画像 {result.Files.Count} 枚）"
            + (noSubject > 0 ? $"。状態量が空の表示灯 {noSubject} 個は右の欄で設定してください" : "")
            + (newPanelPath != null ? "。パネルファイルはまだ保存していません（Ctrl+S）" : ""));
        if (interactive && result.Warnings.Count > 0)
            MessageBox.Show(this, "作りましたが、次の点に注意してください。\n\n" + string.Join("\n", result.Warnings.Take(15).Select(w => "・" + w)), AppName, MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    private void OnCropUpdate(object? sender, RoutedEventArgs? e) => RunCropUpdate(interactive: true);

    /// <summary>今の位置と画像の大きさの範囲で、切り抜き元から切り出し直す。</summary>
    private PsdUpdatePlan? RunCropUpdate(bool interactive)
    {
        if (_doc == null || PanelDir == null) return null;
        FlushReparse();
        if (!_doc.HasCropLink)
        {
            Message("切り抜き元とのつながりがありません（画像から作った表示灯だけを更新できます）");
            return null;
        }
        var panelDir = PanelDir;
        PsdUpdatePlan plan;
        Mouse.OverrideCursor = Cursors.Wait;
        try { plan = CropUpdate.Plan(_doc, panelDir, ImageLoader.TryLoadPixels); }
        finally { Mouse.OverrideCursor = null; }

        if (interactive)
        {
            var msg = $"切り抜き元から画像 {plan.Files.Count} 枚を切り出し直します（今の画像は上書きされ、元に戻せません）。\n"
                + string.Join("\n", plan.Files.Take(8).Select(f => "・" + PsdImport.PathFrom(panelDir, f.FullPath)))
                + (plan.Files.Count > 8 ? $"\n…ほか {plan.Files.Count - 8} 枚" : "");
            if (plan.Skipped.Count > 0)
                msg += "\n\n次の表示灯は画像を手で差し替えているので、切り出しません:\n" + string.Join("\n", plan.Skipped.Take(8).Select(m => "・" + m));
            if (plan.Missing.Count > 0)
                msg += "\n\n切り出せないもの:\n" + string.Join("\n", plan.Missing.Take(8).Select(m => "・" + m));
            if (plan.Warnings.Count > 0)
                msg += "\n\n注意:\n" + string.Join("\n", plan.Warnings.Take(5).Select(m => "・" + m));
            if (MessageBox.Show(this, msg, "切り抜き元から更新", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return null;
        }
        try
        {
            foreach (var f in plan.Files) WriteNewFile(f.FullPath, f.Png);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"画像を書き出せませんでした:\n{ex.Message}");
            return null;
        }
        _cache.Clear();
        RebuildAll();
        Message($"切り抜き元から更新しました（画像 {plan.Files.Count} 枚"
            + (plan.Skipped.Count > 0 ? $"、手で差し替えた画像のため触らなかった表示灯 {plan.Skipped.Count} 個" : "")
            + (plan.Missing.Count > 0 ? $"、切り出せないもの {plan.Missing.Count} 個" : "") + "）");
        return plan;
    }
}
