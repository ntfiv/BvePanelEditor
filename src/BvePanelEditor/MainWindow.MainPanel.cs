using System.IO;
using System.Windows;
using BvePanelEditor.Core;
using BvePanelEditor.Core.Crop;
using BvePanelEditor.Core.Psd;
using BvePanelEditor.Rendering;
using BvePanelEditor.Views;

namespace BvePanelEditor;

/// <summary>
/// メインパネル: パネルの土台にする、常表示の表示灯。[This] には画像を書かない
/// （メインパネルより下に窓などを置けるように）。解像度・表示範囲はメインパネルの画像から決める。
/// </summary>
public partial class MainWindow
{
    /// <summary>画像の中身の大きさ（パネルくんが 2 の累乗に広げた PNG なら広げる前）。読めなければ null。</summary>
    private static (int W, int H)? ContentSize(string full)
    {
        if (PngWriter.TryReadContentSize(full, out var cw, out var ch)) return (cw, ch);
        return ImageLoader.TryLoadPixels(full) is { } px ? (px.Width, px.Height) : null;
    }

    private string? PanelFull(string? rel) => PanelDir != null && !string.IsNullOrWhiteSpace(rel) ? Path.GetFullPath(Path.Combine(PanelDir, rel.Trim().Trim('"'))) : null;

    /// <summary>メインパネルの位置と、画像の中身の大きさ。</summary>
    private (int X, int Y, int W, int H)? MainRect(PanelSection main)
    {
        PanelValues.TryPoint(main.Get("Location"), out var x, out var y);
        var size = (PanelFull(main.Get("DaytimeImage")) is { } d ? ContentSize(d) : null) ?? (PanelFull(main.Get("NighttimeImage")) is { } n ? ContentSize(n) : null);
        return size is { } s ? ((int)Math.Round(x), (int)Math.Round(y), s.W, s.H) : null;
    }


    // ---- 切り替える操作 ----

    /// <summary>選んだ表示灯をメインパネルにする。[This] に画像があれば、表示灯に移して [This] から画像を外すか聞く。</summary>
    private void OnMakeMainPanel(object? sender, RoutedEventArgs? e)
    {
        if (_doc == null || _selected < 0) return;
        FlushReparse();
        var sec = _doc.Sections[_selected];
        if (sec.Kind == SectionKind.This) { OnThisToMainPanel(sender, e); return; }
        if (sec.Kind != SectionKind.PilotLamp) { Message("メインパネルにできるのは表示灯 [PilotLamp] です"); return; }
        bool move = false;
        if (_doc.This?.Get("DaytimeImage") is { Length: > 0 } cur && !IsBlankThis())
            move = ChoiceDialog.Show(this, "メインパネルにする", $"[This] の画像「{cur}」をどうしますか？",
            [
                "常表示の表示灯に移して、[This] からは画像を外す（いちばん下に置きます。おすすめ）",
                "[This] の画像はそのままにする",
            ]) == 0;
        MakeMainPanel(_selected, move);
    }

    /// <summary>index の表示灯をメインパネルにする（確認用からも呼ぶ）。</summary>
    public bool MakeMainPanel(int index, bool moveThisImage)
    {
        if (_doc == null) return false;
        var main = _doc.Sections[index];
        int moved = MainPanel.MakeMain(_doc, index, moveThisImage);
        CommitModel();
        _cache.Clear();
        RebuildAll();
        SelectSection(_doc.Sections.IndexOf(main));
        Message($"「{DisplayName(main)}」をメインパネルにしました。"
            + (moved >= 0 ? $"[This] の画像は表示灯「{_doc.Sections[moved].Title}」（レイヤー {_doc.Sections[moved].Get("Layer")}）に移し、[This] からは画像を外しました。" : "")
            + "解像度・表示範囲は [This] の欄でメインパネルから決められます");
        return true;
    }

    /// <summary>[This] の画像をメインパネル（レイヤー 1）にする。</summary>
    private void OnThisToMainPanel(object? sender, RoutedEventArgs? e) => ThisToMainPanel();

    public bool ThisToMainPanel()
    {
        if (_doc?.This is not { } self) return false;
        FlushReparse();
        if (IsBlankThis()) { Message("[This] にメインパネルにする画像がありません"); return false; }
        int before = _doc.Sections.Count(s => s.Kind is SectionKind.PilotLamp or SectionKind.Needle or SectionKind.DigitalNumber or SectionKind.DigitalGauge);
        int at = MainPanel.FromThis(_doc);
        if (at < 0) return false;
        CommitModel();
        _cache.Clear();
        RebuildAll();
        SelectSection(at);
        Message($"[This] の画像をメインパネル（レイヤー {MainPanel.Layer}）にし、[This] からは画像を外しました。"
            + (before > 0 ? "ほかの計器はメインパネルより上になるようにレイヤーを上げています。" : "") + "メインパネルより下（レイヤー 0 など）に窓などを置けます");
        return true;
    }

    private void OnUnsetMainPanel(object? sender, RoutedEventArgs? e)
    {
        if (_doc?.MainPanel is not { } main) return;
        FlushReparse();
        main.IsMainPanel = false;
        CommitModel();
        RebuildAll();
        Message("メインパネルの印を外しました（表示灯はそのまま残っています）");
    }

    /// <summary>[This] に表示に使う画像がないか（画像なし、または前の版のパネルくんが書いた透明な this_blank_*.png）。</summary>
    private bool IsBlankThis() => _doc != null && MainPanel.ThisHasNoImage(_doc);

    /// <summary>[This] の欄の「メインパネル」: 常表示の表示灯から選ぶ（-1 = なし）。</summary>
    private void OnMainPanelPicked(int index)
    {
        if (_doc == null) return;
        FlushReparse();
        if (index < 0) { OnUnsetMainPanel(null, null); return; }
        _selected = index;
        _selSet = [index];
        OnMakeMainPanel(null, null);
    }

    /// <summary>解像度をメインパネルの画像の幅にする。</summary>
    private void OnResolutionFromMain()
    {
        if (_doc?.This is not { } self || _doc.MainPanel is not { } main) { Message("メインパネルがありません"); return; }
        FlushReparse();
        if (MainRect(main) is not { } r) { Warn("メインパネルの画像を読めませんでした。"); return; }
        self.Set("Resolution", r.W.ToString());
        CommitModel();
        Message($"解像度をメインパネルの画像の幅 {r.W} にしました");
    }

    /// <summary>表示範囲を計算する元の絵: メインパネルがあればその画像を位置どおりに置いたもの、なければ [This] の画像。</summary>
    private (PsdPixels? Day, PsdPixels? Night) ViewLimitSource()
    {
        if (_doc?.MainPanel is { } main && MainRect(main) is { } r)
        {
            PsdPixels? Place(string key)
            {
                if (PanelFull(main.Get(key)) is not { } f || ImageLoader.TryLoadPixels(f) is not { } px) return null;
                // パネルの座標（0, 0 から）にそろえる。広げた余白は除く
                var content = RegionDetect.CropOf(px, new PsdRect(0, 0, r.W, r.H));
                return RegionDetect.CropOf(new PsdPixels(new PsdRect(r.X, r.Y, r.X + r.W, r.Y + r.H), content.Bgra), new PsdRect(0, 0, r.X + r.W, r.Y + r.H));
            }
            return (Place("DaytimeImage"), Place("NighttimeImage"));
        }
        var self = _doc?.This;
        PsdPixels? Load(string key) => PanelFull(self?.Get(key)) is { } f ? ImageLoader.TryLoadPixels(f) : null;
        return (Load("DaytimeImage"), Load("NighttimeImage"));
    }
}
