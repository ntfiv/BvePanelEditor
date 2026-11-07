using System.IO;
using BvePanelEditor.Core;
using BvePanelEditor.Core.Psd;
using BvePanelEditor.Rendering;

namespace BvePanelEditor;

/// <summary>
/// 確認用: フォルダの下のパネルのデジタル数字で、画像から見つけた 1 コマの高さと、書いてある Interval を比べる
/// （--digittest フォルダ 結果.txt。ファイルは読むだけ）。
/// </summary>
public partial class MainWindow
{
    public void RunDigitTest(string dir, string report)
    {
        var log = new List<string>();
        int total = 0, same = 0, differ = 0, none = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                         .Where(p => p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)))
            {
                string text;
                try { text = BveText.Read(f).Text; } catch (IOException) { continue; }
                if (BveFiles.Detect(text) != BveFileKind.Panel) continue;
                var doc = PanelDocument.Parse(text);
                foreach (var s in doc.Sections.Where(s => s.Kind == SectionKind.DigitalNumber))
                {
                    if (s.Get("DaytimeImage") is not { Length: > 0 } rel) continue;
                    var img = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(f)!, rel.Trim().Trim('"')));
                    int written = (int)PanelValues.Double(s.Get("Interval"), 0);
                    if (!seen.Add(img + "|" + written)) continue; // 同じ画像・同じ値は 1 回だけ
                    var px = ImageLoader.TryLoadPixels(img);
                    if (px == null) continue;
                    int? content = PngWriter.TryReadContentSize(img, out _, out var ch) ? ch : null;
                    var r = DigitalFrames.Detect(px, content);
                    total++;
                    string mark;
                    if (r == null) { none++; mark = "なし"; }
                    else if (r.Interval == written) { same++; mark = "一致"; }
                    else { differ++; mark = "違う"; }
                    if (mark != "一致" && DigitalFrames.LastBands is { } bl)
                        log.Add($"        背景 {(DigitalFrames.LastBackground is { Count: > 0 } bgc ? string.Join("・", bgc.Select(c => $"#{c.R:X2}{c.G:X2}{c.B:X2}")) : "透明")}、帯 {bl.Count}: " + string.Join(" ", bl.Take(14).Select(b => $"{b.Top}-{b.Bottom}")));
                    log.Add($"{mark}\t書いてある {written}\t見つけた {r?.Interval.ToString() ?? "-"}（{r?.Frames} コマ{(r?.Confident == false ? "・推測" : "")}）\t{px.Width}×{px.Height}\t{Path.GetRelativePath(dir, img)}");
                }
            }
        }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); }
        log.Insert(0, $"デジタル数字の画像 {total} 枚: 一致 {same}、違う {differ}、見つけられない {none}");
        File.WriteAllLines(report, log);
        Close();
    }
}

public partial class MainWindow
{
    /// <summary>確認用: 開いたパネルのデジタル数字それぞれで「1 コマの高さを画像から求める」を押したのと同じことをする（--intervaltest 結果.txt。保存しない）。</summary>
    public void RunIntervalTest(string report)
    {
        var log = new List<string>();
        int asked = 0;
        _autoConfirm = _ => { asked++; return false; }; // 置き換えるか聞かれたら「いいえ」
        try
        {
            for (int i = 0; i < _doc!.Sections.Count; i++)
            {
                var s = _doc.Sections[i];
                if (s.Kind != SectionKind.DigitalNumber) continue;
                var before = s.Get("Interval");
                int askedBefore = asked;
                SelectForStartup(i);
                OnIntervalRequested();
                log.Add($"#{i + 1} {s.Title}: 前 {before} → 後 {_doc.Sections[i].Get("Interval")}{(asked > askedBefore ? "（置き換えるか聞かれて「いいえ」）" : "")}　｜ {MessageText.Text}");
            }
        }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); }
        File.WriteAllLines(report, log);
        _savedText = Editor.Text;
        Close();
    }
}
