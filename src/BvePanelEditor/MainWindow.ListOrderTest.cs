using System.IO;
using BvePanelEditor.Core;

namespace BvePanelEditor;

/// <summary>確認用: オブジェクト一覧の並び順（--listordertest 作業フォルダ 画面.png 結果.txt）。設定ファイルには書かない。</summary>
public partial class MainWindow
{
    public async void RunListOrderTest(string workDir, string png, string report)
    {
        var log = new List<string>();
        int fail = 0;
        void Check(string name, bool ok) { log.Add($"{(ok ? "OK  " : "FAIL")} {name}"); if (!ok) fail++; }
        bool layerBefore = _layerOrder;
        try
        {
            Directory.CreateDirectory(workDir);
            string Lamp(string name, int layer) => $"\r\n;{name}\r\n[PilotLamp]\r\nSubject = true\r\nLocation = 0, 0\r\nLayer = {layer}\r\n";
            var text = "BveTs Instrument Panel 2.00\r\n\r\n[This]\r\nResolution = 100\r\n"
                + Lamp("A", 1)
                + "\r\n;===== グループ: G =====" + Lamp("B", 5) + Lamp("C", 2) + ";===== グループ終わり: G =====\r\n"
                + Lamp("D", 3) + Lamp("E", 5);
            var panel = Path.Combine(workDir, "panel.txt");
            File.WriteAllText(panel, text);
            LoadPanel(panel, null);
            await Task.Delay(300);
            List<string> Rows() => (ElementList.ItemsSource as IEnumerable<ElementRow>)!.Select(r => r.IsHeader ? $"[{r.GroupName}]" : r.Index == 0 ? "This" : r.Title).ToList();

            SetListOrder(false, save: false);
            log.Add("     ファイル順: " + string.Join(" ", Rows()));
            Check("ファイル順はパネルファイルの順", Rows().SequenceEqual(["This", "A", "[G]", "B", "C", "D", "E"]));

            SetListOrder(true, save: false);
            Check("レイヤー順のボタンだけがへこむ", OrderLayerButton.IsChecked == true && OrderFileButton.IsChecked == false);
            log.Add("     レイヤー順: " + string.Join(" ", Rows()));
            Check("レイヤー順: 手前ほど上・同じレイヤーはファイルの後ろが上・[This] はいちばん下・グループは崩さない（中もレイヤー順）",
                Rows().SequenceEqual(["E", "[G]", "B", "C", "D", "A", "This"]));
            Check("テキストは変わらない", Editor.Text == text);

            // ↑ はレイヤーを上げる
            SelectSection(_doc!.Sections.FindIndex(s => s.Title == "D"));
            OnMoveUp(this, new System.Windows.RoutedEventArgs());
            await Task.Delay(200);
            Check("レイヤー順の ↑ はレイヤーを 1 つ上げる（D: 3 → 4）", _doc!.Sections.First(s => s.Title == "D").Get("Layer") == "4");
            OnMoveUp(this, new System.Windows.RoutedEventArgs());
            OnMoveUp(this, new System.Windows.RoutedEventArgs());
            await Task.Delay(200);
            log.Add("     D を 6 に上げたあと: " + string.Join(" ", Rows()));
            Check("D をいちばん手前（6）に上げると、一覧のいちばん上へ", Rows()[0] == "D" && _doc.Sections.First(s => s.Title == "D").Get("Layer") == "6");
            // グループを折りたたんでも並びは同じ
            _folded.Add("G");
            RebuildAll();
            log.Add("     G を折りたたむ: " + string.Join(" ", Rows()));
            Check("グループを折りたたむと中のオブジェクトを隠す", Rows().SequenceEqual(["D", "E", "[G]", "A", "This"]));
            _folded.Remove("G");
            RebuildAll();
            UpdateLayout();
            await Task.Delay(300);
            SaveWindowPng(this, png);
        }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); fail++; }
        SetListOrder(layerBefore, save: false);
        log.Add(fail == 0 ? "ALL PASSED" : $"{fail} FAILED");
        File.WriteAllLines(report, log);
        _savedText = Editor.Text;
        Close();
    }
}
