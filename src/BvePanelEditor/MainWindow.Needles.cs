using System.Globalization;
using System.IO;
using System.Windows;
using BvePanelEditor.Core;
using BvePanelEditor.Views;

namespace BvePanelEditor;

/// <summary>指針を置く（標準の針・ほかの針の画像）。</summary>
public partial class MainWindow
{
    private void OnAddStandardNeedle(object? sender, RoutedEventArgs? e)
    {
        if (_doc == null) { Message("先にパネルを開くか作ってください"); return; }
        if (PanelDir == null) { Warn("先にパネルファイルを保存してください（針の画像をパネルのフォルダにコピーするため）。"); return; }
        var w = new NeedlePickerWindow(this);
        if (w.ShowDialog() != true || w.Result == null) return;
        PlaceNeedles(w.Result, Canvas.ViewCenterPanel, w.ResultGroupName);
    }

    /// <summary>
    /// 針を置く。画像はパネルの needle フォルダにコピーする（同じ中身があればそれを使う）。置いたオブジェクトの番号を返す。
    /// 2 本以上（圧力計Ａ・時計などの組）は groupName（なければ「指針」）でグループにする。
    /// </summary>
    public List<int> PlaceNeedles(List<NeedleSpec> needles, Point at, string? groupName = null)
    {
        if (_doc == null || PanelDir == null) return [];
        FlushReparse();
        var dir = Path.Combine(PanelDir, "needle");
        int layer = _doc.Sections.Where(s => s.Kind is SectionKind.PilotLamp or SectionKind.Needle or SectionKind.DigitalNumber or SectionKind.DigitalGauge)
            .Select(s => (int)Math.Round(PanelValues.Double(s.Get("Layer"), 0))).DefaultIfEmpty(0).Max() + 1;
        var place = GetInsertPlace(); // 選んでいるオブジェクトのすぐ下（手前）に、そのレイヤーから順に
        string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        var added = new List<int>();
        var copied = new List<string>();
        try
        {
            foreach (var n in needles)
            {
                var day = CopyIntoPanel(n.DayFile, dir, copied);
                var night = n.NightFile != null && File.Exists(n.NightFile) ? CopyIntoPanel(n.NightFile, dir, copied) : null;
                var sec = PanelSection.Create(SectionKind.Needle);
                sec.SetTitle(n.Title);
                sec.Set("Subject", n.Subject);
                sec.Set("Location", PanelValues.Point(Math.Round(at.X) + n.OffsetX, Math.Round(at.Y) + n.OffsetY));
                sec.Set("DaytimeImage", day);
                if (night != null) sec.Set("NighttimeImage", night);
                sec.Set("Origin", PanelValues.Point(n.OriginX, n.OriginY));
                sec.Set("Layer", (layer++).ToString()); // 時計なら時・分・秒の順に手前へ
                sec.Set("Radius", F(n.Radius));
                sec.Set("InitialAngle", F(n.InitialAngle));
                sec.Set("LastAngle", F(n.LastAngle));
                sec.Set("Minimum", F(n.Minimum));
                sec.Set("Maximum", F(n.Maximum));
                if (n.NaturalFreq > 0) sec.Set("NaturalFreq", F(n.NaturalFreq));
                if (n.DampingRatio > 0) sec.Set("DampingRatio", F(n.DampingRatio));
                if (n.TransparentColor != null) sec.Set("TransparentColor", n.TransparentColor);
                // StopPin は書式 2.01 で足されたキーだが、ヘッダが 2.00 でも BVE（5.8 以降・6）は読む
                if (n.StopPin) sec.Set("StopPin", "True");
                added.Add(InsertNewSection(sec, place, added.Count, layerOffset: added.Count));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"針の画像をコピーできませんでした:\n{ex.Message}");
            return [];
        }
        // 組で置いた針はグループにする（まとめて動かす・選ぶため）
        string? group = null;
        if (added.Count > 1)
        {
            added = _doc.MakeGroup(added, string.IsNullOrWhiteSpace(groupName) ? "指針" : groupName.Trim());
            group = _doc.Sections[added[0]].Group;
        }
        CommitModel();
        _cache.Clear();
        RebuildAll();
        if (added.Count > 0) ApplySelection(added[0], added, group, SelectionSource.Other);
        // 置いた針だけは、選んでいる間は位置ロック中でも動かせる（ほかを選ぶとロックに戻る）
        Canvas.AllowFreshEdit(added);
        Canvas.Focus();
        Message($"指針を {added.Count} 本置きました（{string.Join("・", needles.Select(n => n.Title))}）。"
            + (group != null ? $"グループ「{group}」にしました。" : "")
            + (copied.Count > 0 ? $"画像 {copied.Count} 枚を needle フォルダにコピーしました。" : "")
            + "選んでいる間はこの針だけ動かせるので、ドラッグで文字盤の中心へ動かし、黄色い四角のドラッグか ＋／－ キーで大きさ（半径）を合わせてください（ほかを選ぶとロックに戻ります）。"
            + "合わせたら、左上の「この針の蓋を作る」で軸の蓋を針の上に置けます"
            + (added.Count > 1 ? $"（{added.Count} 本まとめて変わります）" : ""));
        return added;
    }

    /// <summary>画像を dir にコピーして、パネルからの相対パスを返す。同じ名前で同じ中身があればそれを使い、違う中身なら名前に _2 などを付ける。</summary>
    private string CopyIntoPanel(string src, string dir, List<string> copied)
    {
        var full = Path.GetFullPath(src);
        // すでにパネルのフォルダの中にある画像なら、コピーせずにそのまま使う
        if (full.StartsWith(Path.GetFullPath(PanelDir!) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return PropertyEditor.RelativePath(PanelDir!, full);
        var bytes = File.ReadAllBytes(full);
        var name = Path.GetFileNameWithoutExtension(full);
        var ext = Path.GetExtension(full);
        for (int i = 1; ; i++)
        {
            var target = Path.Combine(dir, i == 1 ? name + ext : $"{name}_{i}{ext}");
            if (File.Exists(target))
            {
                if (File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) return PropertyEditor.RelativePath(PanelDir!, target);
                continue;
            }
            WriteNewFile(target, bytes);
            copied.Add(target);
            return PropertyEditor.RelativePath(PanelDir!, target);
        }
    }
}
