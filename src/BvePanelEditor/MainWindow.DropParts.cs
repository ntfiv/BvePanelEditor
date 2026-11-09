using System.IO;
using System.Windows;
using System.Windows.Threading;
using BvePanelEditor.Core;
using BvePanelEditor.Rendering;
using BvePanelEditor.Views;

namespace BvePanelEditor;

/// <summary>画像をドロップしたとき、今のパネルに部品（表示灯・指針・デジタル数字）として置く。</summary>
public partial class MainWindow
{
    private void OnDropFile(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        // パネル画面の上に落としたなら、その位置に置く
        var sp = e.GetPosition(Canvas);
        Point? at = sp.X >= 0 && sp.Y >= 0 && sp.X <= Canvas.ActualWidth && sp.Y <= Canvas.ActualHeight ? Canvas.ToPanel(sp) : null;
        // ドロップの処理中にダイアログを出すとドラッグ元のアプリが固まるので、ドロップを終えてから開く
        Dispatcher.BeginInvoke(() => HandleDrop(files, at), DispatcherPriority.Background);
    }

    private static bool IsImageFile(string f) => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant());

    /// <summary>
    /// ドロップしたファイル。パネルを開いていて画像・PSD だけなら、常点灯画像・デジタル数字（コマ割済／単コマ）のどれとして読み込むか聞く。
    /// パネルを開いていなければ、今までどおり開く（画像・PSD は画像からパネルを作るなど）。
    /// </summary>
    public void HandleDrop(string[] files, Point? at)
    {
        var images = files.Where(f => IsImageFile(f) || IsPsdFile(f)).ToList();
        if (_doc == null || images.Count == 0 || images.Count != files.Length)
        {
            OpenPath(files[0]);
            return;
        }
        ImportImagesToPanel(images, at);
    }

    /// <summary>画像を表示灯・デジタル数字として置く。at（パネルの座標）を中心に、2 枚目からは少しずつずらす。置いた計器の番号を返す。</summary>
    public List<int> PlaceImageParts(IReadOnlyList<string> images, SectionKind kind, Point? at)
    {
        if (_doc == null) return [];
        if (PanelDir == null) { Warn("先にパネルファイルを保存してください（画像をパネルからの場所で指すため）。"); return []; }
        FlushReparse();
        var c = at ?? Canvas.ViewCenterPanel;
        int layer = _doc.Sections.Where(s => s.Kind is SectionKind.PilotLamp or SectionKind.Needle or SectionKind.DigitalNumber or SectionKind.DigitalGauge)
            .Select(s => (int)Math.Round(PanelValues.Double(s.Get("Layer"), 0))).DefaultIfEmpty(0).Max() + 1;
        var added = new List<int>();
        var copied = new List<string>();
        var notes = new List<string>();
        try
        {
            for (int k = 0; k < images.Count; k++)
            {
                var full = Path.GetFullPath(images[k]);
                var px = ImageLoader.TryLoadPixels(full);
                if (px == null) { notes.Add($"「{Path.GetFileName(full)}」を読めませんでした"); continue; }
                var day = ImageForPanel(full, copied);
                var nightFile = NeedlePickerWindow.FindNight(full);
                var night = nightFile != null ? ImageForPanel(nightFile, copied) : null;
                var sec = PanelSection.Create(kind);
                sec.SetTitle(Path.GetFileNameWithoutExtension(full));
                double ox = c.X + k * 20, oy = c.Y + k * 20;
                if (kind == SectionKind.DigitalNumber)
                {
                    int? content = Core.Psd.PngWriter.TryReadContentSize(full, out _, out var ch) ? ch : null;
                    var found = DigitalFrames.Detect(px, content);
                    int interval = found?.Interval ?? (px.Height >= 10 ? px.Height / 10 : px.Height);
                    sec.Set("Subject", "power");
                    sec.Set("Location", PanelValues.Point(Math.Round(ox - px.Width / 2.0), Math.Round(oy - interval / 2.0)));
                    sec.Set("DaytimeImage", day);
                    if (night != null) sec.Set("NighttimeImage", night);
                    sec.Set("Interval", interval.ToString());
                    if (found is { Confident: false }) notes.Add($"「{Path.GetFileName(full)}」の 1 コマの高さ {interval} は推定です");
                }
                else
                {
                    sec.Set("Subject", "true");
                    sec.Set("Location", PanelValues.Point(Math.Round(ox - px.Width / 2.0), Math.Round(oy - px.Height / 2.0)));
                    sec.Set("DaytimeImage", day);
                    if (night != null) sec.Set("NighttimeImage", night);
                }
                sec.Set("Layer", layer.ToString());
                JoinViewVariation(sec);
                _doc.Sections.Add(sec);
                added.Add(_doc.Sections.Count - 1);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"画像をパネルのフォルダにコピーできませんでした:\n{ex.Message}");
        }
        if (added.Count == 0) { if (notes.Count > 0) Warn(string.Join("\n", notes)); return []; }
        CommitModel();
        _cache.Clear();
        RebuildAll();
        ApplySelection(added[0], added, null, SelectionSource.Other);
        Canvas.AllowFreshEdit(added);
        Canvas.Focus();
        var label = kind == SectionKind.DigitalNumber ? "デジタル数字" : "表示灯（常表示）";
        Message($"{label}を {added.Count} 個置きました。"
            + (copied.Count > 0 ? $"パネルのフォルダの外の画像 {copied.Count} 枚は、パネルのフォルダにコピーしました。" : "")
            + (notes.Count > 0 ? string.Join("。", notes) + "。" : "")
            + "状態量やレイヤーは右の欄で直せます。追加した計器は、選んでいる間だけ位置ロック中でも動かせます（ほかを選ぶとロックに戻ります）");
        return added;
    }

    /// <summary>針の画像をドロップした: 指針を置く画面を、その画像で開く。</summary>
    private void PlaceNeedleFromImage(string image, Point? at)
    {
        if (PanelDir == null) { Warn("先にパネルファイルを保存してください（画像をパネルからの場所で指すため）。"); return; }
        var w = new NeedlePickerWindow(this, image);
        if (w.ShowDialog() != true || w.Result == null) return;
        PlaceNeedles(w.Result, at ?? Canvas.ViewCenterPanel, w.ResultGroupName);
    }

    /// <summary>パネルのフォルダの中の画像ならそのまま、外ならパネルのフォルダにコピーして、パネルからの相対パスを返す。</summary>
    private string ImageForPanel(string full, List<string> copied) => CopyIntoPanel(full, PanelDir!, copied);
}
