using System.IO;
using System.Windows;
using BvePanelEditor.Core;

namespace BvePanelEditor;

/// <summary>
/// 確認用: グループの入れ子（--grouptest 作業フォルダ 画面.png 結果.txt）。
/// パネル画面のクリック・ダブルクリック（1 つずつ内側へ）、中に入ったままのグループ化、一覧の字下げ・折りたたみ、並べ替え・保存を確かめる。
/// </summary>
public partial class MainWindow
{
    public async void RunGroupTest(string workDir, string png, string report)
    {
        var log = new List<string>();
        int fail = 0;
        void Check(string name, bool ok) { log.Add($"{(ok ? "OK  " : "FAIL")} {name}"); if (!ok) fail++; }
        try
        {
            Directory.CreateDirectory(workDir);
            var px = new byte[40 * 40 * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i + 1] = 200; px[i + 3] = 255; }
            File.WriteAllBytes(Path.Combine(workDir, "lamp.png"), Core.Psd.PngWriter.Encode(40, 40, px));
            string Lamp(string name, int x) => $"\r\n;{name}\r\n[PilotLamp]\r\nSubject = true\r\nLocation = {x}, 100\r\nDaytimeImage = lamp.png\r\nLayer = 1\r\n";
            var text = "BveTs Instrument Panel 2.00\r\n\r\n[This]\r\nResolution = 600\r\n" + Lamp("A", 100) + Lamp("B", 200) + Lamp("C", 300) + Lamp("D", 400) + Lamp("E", 500);
            var panel = Path.Combine(workDir, "panel.txt");
            File.WriteAllText(panel, text);
            LoadPanel(panel, null);
            await Task.Delay(300);
            Canvas.FitAll();
            UpdateLayout();
            _autoConfirm = _ => true;
            Point At(int x) => new(x + 20, 120);
            string Paths() => string.Join(",", _doc!.Sections.Skip(1).Select(s => s.Title + (s.Groups.Count > 0 ? "[" + string.Join("/", s.Groups) + "]" : "")));
            string Sel() => (_selGroup != null ? $"G:{_selGroup}" : string.Join("+", _selSet.Select(i => _doc!.Sections[i].Title))) + (Canvas.EnteredGroup != null ? $" 中:{Canvas.EnteredGroup}" : "");
            void Click(int x, bool dbl = false, bool shift = false) => Canvas.SelectAt(At(x), dbl, shift);

            // 1. B C D をグループ「外」に、その中の C D を「中」に（中に入ったまま Ctrl+G）
            ApplySelection(2, [2, 3, 4], null, SelectionSource.Other);
            OnMakeGroup(null, null);
            RenameGroup(_selGroup!, "外");
            Click(300);                       // 外ごと
            Click(300, dbl: true);           // 外の中に入って C
            Click(400, shift: true);         // C と D
            log.Add("     中に入って C と D を選んだ: " + Sel());
            Check("中に入ったまま、C と D を Shift＋クリックで選べる", Sel() == "C+D 中:外");
            OnMakeGroup(null, null);
            RenameGroup(_selGroup!, "中");
            log.Add("     並び: " + Paths());
            Check("中に入ったまま Ctrl+G で、外の中にグループ「中」ができる", Paths() == "A,B[外],C[外/中],D[外/中],E");
            Check("できたグループを選び、外の中に入ったまま", Sel() == "G:中 中:外");

            // 2. パネル画面のクリック: 最初はいちばん外、ダブルクリックのたびに 1 つ内側へ
            Click(100);
            Check("グループの外のオブジェクトはそのまま選ぶ", Sel() == "A");
            Click(300);
            Check("クリックはいちばん外のグループ（外）", Sel() == "G:外");
            Click(300, dbl: true);
            Check("ダブルクリックで外の中に入り、すぐ中の「中」を選ぶ", Sel() == "G:中 中:外");
            Click(300, dbl: true);
            Check("もう一度ダブルクリックで「中」の中に入り、C だけ", Sel() == "C 中:中");
            Click(400);
            Check("中に入ったまま隣の D", Sel() == "D 中:中");
            Click(200);
            Check("中の外の B をクリックすると、外の中に入ったまま B", Sel() == "B 中:外");
            Click(300);
            Check("外の中で「中」のオブジェクトをクリックすると「中」ごと", Sel() == "G:中 中:外");
            // 「中」に入った状態で、一覧から外の直下の B を選ぶと、外の中に入ったまま（中の枠は残さない）
            Click(300, dbl: true);
            Click(300, dbl: true);
            Canvas.Select(2);
            Check("入れ子の外側のオブジェクトを選ぶと、それを含む外側のグループの中に入ったままにする", Canvas.EnteredGroup == "外");
            Click(500);
            Check("グループの外をクリックすると抜ける", Sel() == "E");

            // 3. 囲んで選ぶ: 外を丸ごと囲めば外ごと
            Canvas.SelectInRect(new Rect(150, 50, 300, 150), add: false);
            SelectSection(Canvas.Selected, fromCanvas: true);
            Check("外を丸ごと囲むと外ごと選ぶ", Sel() == "G:外");

            // 4. 一覧: 見出しは入れ子の深さだけ字下げ、オブジェクトも深さだけ字下げ
            var rows = Rows;
            string R(ElementRow r) => r.IsHeader ? $"▼{r.GroupName}@{r.HeaderMargin.Left:0}" : $"{_doc!.Sections[r.Index].Title}@{r.Indent.Left:0}";
            log.Add("     一覧: " + string.Join(" ", rows.Select(R)));
            Check("一覧は 外 → B → 中 → C D の順で、深さだけ字下げ", string.Join(" ", rows.Select(R)) == "[This]@0 A@0 ▼外@0 B@14 ▼中@14 C@28 D@28 E@0".Replace("[This]", _doc!.Sections[0].Title));
            // 外を折りたたむと、中の見出しも C・D も出ない
            _folded.Add("外");
            RebuildAll();
            Check("外を折りたたむと、中の見出しも中のオブジェクトも隠れる", string.Join(" ", Rows.Select(R)) == $"{_doc.Sections[0].Title}@0 A@0 ▼外@0 E@0");
            ApplySelection(3, [3], null, SelectionSource.Other); // C を選ぶと開く
            Check("折りたたんだ中のオブジェクトを選ぶと、外側から開く", !_folded.Contains("外") && Rows.Any(r => !r.IsHeader && r.Index == 3));
            SaveWindowPng(this, png);

            // 5. 並べ替え: 中は外の中だけで動く
            ApplySelection(3, _doc.Members("中"), "中", SelectionSource.Other);
            Move(-1);
            Check("↑ で「中」が B の前へ（外の中）", Paths() == "A,C[外/中],D[外/中],B[外],E");
            Move(-1);
            Check("外の最初からは出ない", Paths() == "A,C[外/中],D[外/中],B[外],E");

            // 6. 複製・解除・保存して開き直す
            ApplySelection(2, _doc.Members("外"), "外", SelectionSource.Other);
            OnDuplicate(null, null);
            log.Add("     複製のあと: " + Paths());
            Check("外を複製すると、中も「中 のコピー」として複製する", Paths().Contains("C[外 のコピー/中 のコピー]"));
            Check("保存できる", Save(_path));
            var saved = Editor.Text;
            LoadPanel(_path!, null);
            Check("開き直しても同じ（入れ子の開始行・終了行）", Editor.Text == saved && _doc.GroupWarnings.Count == 0);
            ApplySelection(2, _doc.Members("外"), "外", SelectionSource.Other);
            OnUngroup(null, null);
            Check("外を解除しても、中のグループは残る", Paths().StartsWith("A,C[中],D[中],B,"));
        }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); fail++; }
        _autoConfirm = null;
        log.Add(fail == 0 ? "ALL PASSED" : $"{fail} FAILED");
        File.WriteAllLines(report, log);
        _savedText = Editor.Text;
        Close();
    }
}
