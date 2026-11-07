using System.IO;

namespace BvePanelEditor;

/// <summary>確認用: Ctrl+9（パスをファイル名だけにする）の前後の表示（--pathnamestest 結果.txt。保存しない）。</summary>
public partial class MainWindow
{
    public void RunPathNamesTest(string report)
    {
        var log = new List<string> { "前: " + Title };
        int i = _doc!.Sections.IndexOf(_doc.This!);
        SelectForStartup(i);
        TogglePathNames();
        log.Add("後: " + Title);
        log.Add("右の欄の昼画像: " + string.Join(" / ", FindTextBoxes(PropHost).Where(b => b.IsReadOnly).Select(b => b.Text)));
        TogglePathNames();
        log.Add("戻す: " + Title);
        log.Add(PathDisplay.NamesOnly ? "FAIL 戻らない" : "OK");
        File.WriteAllLines(report, log);
        _savedText = Editor.Text;
        Close();
    }

    private static IEnumerable<System.Windows.Controls.TextBox> FindTextBoxes(System.Windows.DependencyObject root)
    {
        for (int k = 0; k < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); k++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(root, k);
            if (c is System.Windows.Controls.TextBox t) yield return t;
            foreach (var x in FindTextBoxes(c)) yield return x;
        }
    }
}
