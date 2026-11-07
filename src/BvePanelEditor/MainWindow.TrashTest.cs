using System.IO;
using System.Windows;
using BvePanelEditor.Core;
using BvePanelEditor.Core.Psd;
using BvePanelEditor.Views;

namespace BvePanelEditor;

/// <summary>
/// 確認用: パネルを保存しないで閉じるとき、書き出した画像をゴミ箱に送るか聞く（--trashtest 作業フォルダ 結果.txt）。
/// 作業フォルダに確認用のパネルと画像を作って使う。ゴミ箱には送らず、確認用に消す。
/// </summary>
public partial class MainWindow
{
    public async void RunTrashTest(string workDir, string report)
    {
        var log = new List<string>();
        int fail = 0;
        void Check(string name, bool ok) { log.Add($"{(ok ? "OK  " : "FAIL")} {name}"); if (!ok) fail++; }
        var trashed = new List<string>();
        TrashForTest = p =>
        {
            trashed.Add(p);
            if (File.Exists(p)) File.Delete(p); else if (Directory.Exists(p)) Directory.Delete(p);
            return true;
        };
        try
        {
            Directory.CreateDirectory(workDir);
            var panel = Path.Combine(workDir, "panel.txt");
            File.WriteAllText(panel, "BveTs Instrument Panel 2.00\r\n\r\n[This]\r\nResolution = 256\r\n");
            // 外の画像（パネルのフォルダの外。読み込むとパネルのフォルダにコピーされる）
            var outside = Path.Combine(Path.GetDirectoryName(workDir)!, "trash_src");
            Directory.CreateDirectory(outside);
            var img = Path.Combine(outside, "lamp_a.png");
            var px = new byte[10 * 10 * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = 200; px[i + 3] = 255; }
            File.WriteAllBytes(img, PngWriter.Encode(10, 10, px));
            // すでにある画像（上書きされても消さない）
            var keep = Path.Combine(workDir, "keep.png");
            File.WriteAllBytes(keep, PngWriter.Encode(10, 10, px));
            LoadPanel(panel, null);
            await Task.Delay(200);

            // 1. 画像を書き出す（デジタル数字の単コマ: digit フォルダを作る ／ 常点灯: パネルのフォルダにコピー）
            DigitFramesWindow.TestAnswer = _ => true;
            DigitTestWindow.TestAnswer = _ => true;
            ChoiceDialog.TestAnswer = (m, _) => m.Contains("どう読み込みますか") ? 2 : 2;
            HandleDrop([img], new Point(50, 50));
            ChoiceDialog.TestAnswer = (m, _) => m.Contains("どう読み込みますか") ? 0 : -1;
            HandleDrop([img], new Point(80, 80));
            var pending = PendingFiles;
            log.Add("     書き出した画像: " + string.Join(" ／ ", pending.Select(p => Path.GetRelativePath(workDir, p))));
            Check("書き出した画像（digit フォルダの画像と、コピーした画像）を覚える", pending.Count == 2
                && pending.Any(p => p.Contains($"{Path.DirectorySeparatorChar}digit{Path.DirectorySeparatorChar}")) && pending.Any(p => Path.GetFileName(p) == "lamp_a.png"));
            Check("すでにあったファイルは覚えない", !pending.Contains(keep, StringComparer.OrdinalIgnoreCase));

            // 2. 保存しないで閉じる → 聞く → 「やめる」なら閉じない・何も消さない
            string? asked = null;
            ChoiceDialog.TestAnswer = (m, items) => { asked = m; return -1; };
            Check("保存しないで閉じるときに聞き、「やめる」なら閉じない", !AskTrashPendingFiles() && asked != null && asked.Contains("2 個") && trashed.Count == 0);
            // 「残す」なら消さない
            ChoiceDialog.TestAnswer = (_, _) => 1;
            Check("「残す」なら消さない", AskTrashPendingFiles() && trashed.Count == 0 && pending.All(File.Exists) && PendingFiles.Count == 0);

            // 3. もう一度書き出して「ゴミ箱に送る」 → 画像と、空になった digit フォルダを送る
            ChoiceDialog.TestAnswer = (m, _) => m.Contains("どう読み込みますか") ? 2 : 2;
            HandleDrop([img], new Point(50, 50));
            var digitDir = Path.Combine(workDir, "digit");
            var second = PendingFiles;
            log.Add("     2 回目に書き出した画像: " + string.Join(" ／ ", second.Select(p => Path.GetRelativePath(workDir, p))));
            ChoiceDialog.TestAnswer = (_, _) => 0;
            Check("「ゴミ箱に送る」なら、書き出した画像を送る", AskTrashPendingFiles() && second.Count > 0 && second.All(p => trashed.Contains(p) && !File.Exists(p)));
            Check("すでにあった画像・前に「残す」にした画像は送らない", File.Exists(keep) && File.Exists(pending.First(p => Path.GetFileName(p) == "lamp_a.png")));
            log.Add("     送ったもの: " + string.Join(" ／ ", trashed.Select(p => Path.GetRelativePath(workDir, p))));

            // 4. 保存したら覚えていたものは忘れる（聞かない）
            ChoiceDialog.TestAnswer = (m, _) => m.Contains("どう読み込みますか") ? 2 : 2;
            HandleDrop([img], new Point(50, 50));
            Check("書き出すと覚える", PendingFiles.Count > 0);
            Check("保存できる", Save(_path));
            asked = null;
            ChoiceDialog.TestAnswer = (m, _) => { asked = m; return 0; };
            int before = trashed.Count;
            Check("保存したあとは聞かない・送らない", AskTrashPendingFiles() && asked == null && trashed.Count == before);

            // 5. 新しく作ったフォルダは、空になれば一緒に送る
            Directory.Delete(digitDir, true);
            ChoiceDialog.TestAnswer = (m, _) => m.Contains("どう読み込みますか") ? 2 : 2;
            HandleDrop([img], new Point(50, 50));
            Check("digit フォルダを作り直した", Directory.Exists(digitDir) && PendingFiles.Count == 1);
            ChoiceDialog.TestAnswer = (_, _) => 0;
            Check("新しく作ったフォルダは、空になれば一緒にゴミ箱に送る", AskTrashPendingFiles() && !Directory.Exists(digitDir) && trashed.Contains(Path.GetFullPath(digitDir)));
        }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); fail++; }
        ChoiceDialog.TestAnswer = null;
        DigitTestWindow.TestAnswer = null;
        DigitFramesWindow.TestAnswer = null;
        TrashForTest = null;
        log.Add(fail == 0 ? "ALL PASSED" : $"{fail} FAILED");
        File.WriteAllLines(report, log);
        _savedText = Editor.Text;
        Close();
    }
}
