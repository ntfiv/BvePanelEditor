using System.IO;

namespace BvePanelEditor;

/// <summary>確認用: 表示範囲を計算する画面を保存して終了する（--viewlimit 画面.png 結果.txt。パネルは書き換えない）。</summary>
public partial class MainWindow
{
    public void RunViewLimitTest(string png, string report)
    {
        string text;
        try
        {
            text = OpenViewLimit(png) ?? "（画面を開けませんでした）";
            // 上下左右を別々に（左 40・上 200・右 40・下 0）
            var sides = OpenViewLimit(Path.ChangeExtension(png, ".sides.png"), w => w.SetMarginsForTest(false, 40, 200, 40, 0));
            text += "\n--- 上下左右を別々に ---\n" + sides;
        }
        catch (Exception ex) { text = "EXCEPTION " + ex; }
        File.WriteAllText(report, text);
        _savedText = Editor.Text;
        Close();
    }
}
