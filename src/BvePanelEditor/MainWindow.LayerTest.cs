using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BvePanelEditor.Core;
using BvePanelEditor.Views;

namespace BvePanelEditor;

/// <summary>確認用: 複数選んでレイヤーを動かす・一覧の右クリックメニュー（--layertest 画面.png 結果.txt。保存しない）。</summary>
public partial class MainWindow
{
    public async void RunLayerTest(string png, string report)
    {
        var log = new List<string>();
        int fail = 0;
        void Check(string name, bool ok) { log.Add($"{(ok ? "OK  " : "FAIL")} {name}"); if (!ok) fail++; }
        try
        {
            var lamps = Enumerable.Range(0, _doc!.Sections.Count).Where(i => _doc.Sections[i].Kind == SectionKind.PilotLamp).Take(2).ToList();
            int a = lamps[0], b = lamps[1];
            _doc.Sections[a].Set("Layer", "5");
            _doc.Sections[b].Set("Layer", "6");
            CommitModel();
            ApplySelection(a, [a, b], null, SelectionSource.Other);
            string L() => $"{_doc.Sections[a].Get("Layer")},{_doc.Sections[b].Get("Layer")}";
            var others = Enumerable.Range(0, _doc.Sections.Count).Where(i => i != a && i != b && _doc.Sections[i].Kind == SectionKind.PilotLamp).Select(i => (int)PanelValues.Double(_doc.Sections[i].Get("Layer"), 0)).ToList();
            log.Add($"     ほかの表示灯のレイヤー: {others.Min()}〜{others.Max()}");

            ChangeLayers(LayerOp.Up);
            Check("1 つ上げる: 5,6 → 6,7（順はそのまま）", L() == "6,7");
            ChangeLayers(LayerOp.Down); ChangeLayers(LayerOp.Down);
            Check("1 つ下げる ×2: → 4,5", L() == "4,5");
            ChangeLayers(LayerOp.Front);
            Check($"最前面へ: ほか（最大 {others.Max()}）より上", L() == $"{others.Max() + 1},{others.Max() + 2}");
            ChangeLayers(LayerOp.Back);
            int lo = Math.Max(0, others.Min() - 2);
            Check($"最背面へ: ほか（最小 {others.Min()}）より下", L() == $"{lo},{lo + 1}");
            for (int k = 0; k < 10; k++) ChangeLayers(LayerOp.Down);
            Check("0 より下には下げない", L() == "0,1");
            log.Add($"     最後のメッセージ: {MessageText.Text}");
            Check("テキストにも入る", Editor.Text.Contains("Layer = 0") && Editor.Text.Contains("Layer = 1"));

            UpdateListMenu();
            Check("右クリックメニュー: グループ化する（2 個）が押せる", ListMenuGroup.IsEnabled && ((string)ListMenuGroup.Header).Contains("2 個"));
            Check("右クリックメニュー: グループに入っていないので解除は押せない", !ListMenuUngroup.IsEnabled);
            Check("右クリックメニュー: レイヤーの項目が押せる", ListMenuLayerUp.IsEnabled && ListMenuLayerBack.IsEnabled);
            Check("右クリックメニュー: 2 個選んでいるので名前の変更は押せない", !ListMenuRename.IsEnabled);

            await Task.Delay(300);
            SaveWindowPng(this, png);
            ListMenu.PlacementTarget = ElementList;
            ListMenu.Placement = PlacementMode.Bottom;
            ListMenu.IsOpen = true;
            await Task.Delay(400);
            ListMenu.UpdateLayout();
            var rtb = new RenderTargetBitmap((int)ListMenu.ActualWidth, (int)ListMenu.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(ListMenu);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(Path.ChangeExtension(png, ".menu.png"))) enc.Save(fs);
            ListMenu.IsOpen = false;
        }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); fail++; }
        log.Add(fail == 0 ? "ALL PASSED" : $"{fail} FAILED");
        File.WriteAllLines(report, log);
        _savedText = Editor.Text;
        Close();
    }
}
