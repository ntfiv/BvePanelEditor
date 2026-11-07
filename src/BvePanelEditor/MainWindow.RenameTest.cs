using System.IO;
using System.Windows;
using System.Windows.Input;
using BvePanelEditor.Core;

namespace BvePanelEditor;

/// <summary>確認用: 一覧の名前の入力欄（--renametest 画面.png 結果.txt。保存しない）。</summary>
public partial class MainWindow
{
    public async void RunRenameTest(string png, string report)
    {
        var log = new List<string>();
        int fail = 0;
        void Check(string name, bool ok) { log.Add($"{(ok ? "OK  " : "FAIL")} {name}"); if (!ok) fail++; }
        try
        {
            int i = Enumerable.Range(0, _doc!.Sections.Count).First(k => _doc.Sections[k].Kind == SectionKind.PilotLamp);
            SelectForStartup(i);
            await Task.Delay(300);
            BeginRename();
            await Task.Delay(300);
            var box = _renameBox;
            Check("名前の入力欄が出る", box != null && box.IsVisible);
            Check("入力欄はパネルくんのウィンドウの中にある（別のウィンドウではない。日本語入力の変換の枠が入力欄の所に出る条件）",
                box != null && PresentationSource.FromVisual(box) == PresentationSource.FromVisual(this));
            Check("入力欄に文字を入れられる（キーボードの入力先になっている）", box != null && box.IsKeyboardFocused);
            box!.Text = "灯10 テスト";
            SaveWindowPng(this, png);
            box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box)!, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
            await Task.Delay(200);
            Check("Enter で名前が変わる", _doc.Sections[i].Title == "灯10 テスト");
            Check("入力欄が消える", _renameBox == null);

            BeginRename();
            await Task.Delay(200);
            _renameBox!.Text = "取り消す名前";
            _renameBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(_renameBox)!, 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
            await Task.Delay(200);
            Check("Esc で取り消せる", _doc.Sections[i].Title == "灯10 テスト" && _renameBox == null);
        }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); fail++; }
        log.Add(fail == 0 ? "ALL PASSED" : $"{fail} FAILED");
        File.WriteAllLines(report, log);
        _savedText = Editor.Text;
        Close();
    }
}
