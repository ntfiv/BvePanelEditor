using System.IO;
using BvePanelEditor.Core;

namespace BvePanelEditor;

/// <summary>確認用: 一覧での表示／非表示（--hiddentest 画面.png 結果.txt。開いたパネルのコピーに対して使う。保存はしない）。</summary>
public partial class MainWindow
{
    public async void RunHiddenTest(string png, string report)
    {
        var log = new List<string>();
        int fail = 0;
        void Check(string name, bool ok) { log.Add($"{(ok ? "OK  " : "FAIL")} {name}"); if (!ok) fail++; }
        try
        {
            var doc = _doc!;
            ShowFrameMenu.IsChecked = false;
            OnToggleFrame(ShowFrameMenu, new System.Windows.RoutedEventArgs());
            SelectForStartup(-1);
            Canvas.FitAll();
            UpdateLayout();
            await Task.Delay(500);
            var before = Grab();
            var text = Editor.Text;
            // その点でいちばん上にある（クリックで選ばれる）オブジェクトを使う
            System.Windows.Point Mid(Rendering.RenderItem it) { var o = it.Outline(); return new(o.Average(q => q.X), o.Average(q => q.Y)); }
            var needle = _scene!.Items.Where(i => i.Kind == SectionKind.PilotLamp && !i.IsMainPanel).First(i => Canvas.HitForTest(Mid(i)) == i.Section);
            var at = Mid(needle);
            int ni = needle.Section;
            log.Add($"     試すオブジェクト: #{ni + 1} {_doc!.Sections[ni].Title} {needle.Subject}");
            Check("非表示にする前はクリックで選べる", Canvas.HitForTest(at) == ni);

            ToggleHidden([ni]);
            UpdateLayout();
            await Task.Delay(400);
            var after = Grab();
            int diff = 0;
            for (int k = 0; k < before.Px.Length; k += 4) if (before.Px[k] != after.Px[k] || before.Px[k + 1] != after.Px[k + 1]) diff++;
            Check("非表示にするとパネル画面に描かない", diff > 50);
            Check("非表示のオブジェクトはクリックで選ばない", Canvas.HitForTest(at) != ni);
            Check("ファイル（テキスト）には書かない", Editor.Text == text);
            var row = (ElementList.ItemsSource as IEnumerable<ElementRow>)!.First(r => r.Index == ni);
            Check("一覧の目のマークが非表示になり、行が薄くなる", row.Hidden && row.Eye == "" && row.RowOpacity < 1);
            SelectAllElements(null, null);
            Check("すべて選択でも非表示のオブジェクトは選ばない", !_selSet.Contains(ni) && _selSet.Count > 0);

            // テキストを直して読み直しても、非表示のまま
            Editor.Document.Insert(Editor.Document.TextLength, "\r\n; メモ\r\n");
            await Task.Delay(700);
            FlushReparse();
            Check("テキストを直しても非表示のまま", _doc!.Sections[ni].ViewHidden);
            SelectSection(ni);
            UpdateLayout();
            await Task.Delay(300);
            SaveWindowPng(this, png);

            // 戻す
            ToggleHidden([ni]);
            Check("もう一度押すと表示に戻る", !_doc.Sections[ni].ViewHidden && Canvas.HitForTest(at) == ni);
            ToggleHidden([ni, 0]);
            OnShowAllMenu(null, null);
            Check("すべて表示する", !_doc.Sections.Any(s => s.ViewHidden));
            // [This] を隠すとパネル本体の画像も描かない
            ToggleHidden([_doc.Sections.IndexOf(_doc.This!)]);
            Check("[This] を非表示にするとパネル本体の画像を描かない", _scene!.ThisHidden);
            OnShowAllMenu(null, null);
        }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); fail++; }
        log.Add(fail == 0 ? "ALL PASSED" : $"{fail} FAILED");
        File.WriteAllLines(report, log);
        _savedText = Editor.Text;
        Close();
    }
}
