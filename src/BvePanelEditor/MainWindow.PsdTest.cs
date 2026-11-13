using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BvePanelEditor.Core;
using BvePanelEditor.Core.Psd;
using BvePanelEditor.Views;

namespace BvePanelEditor;

/// <summary>PSD の読み込みの通し確認（--psdtest / --psdwindow）。PSD は読むだけで、書き出しは workDir に行う。</summary>
public partial class MainWindow
{
    /// <summary>読み込み画面を開いて見本ができるまで待ち、画面を保存して閉じる。</summary>
    public async void ShowPsdWindowSnapshot(string dayPsd, string? nightPsd, string pngPath)
    {
        var dir = Path.Combine(Path.GetTempPath(), "panelkun_psdwindow");
        var w = new PsdImportWindow(this, dir, dayPsd, nightPsd, 5);
        w.Show();
        await Task.Delay(500);
        await w.WaitPreviewAsync();
        await Task.Delay(800);
        SaveWindowPng(w, pngPath);
        w.Close();
        Close();
    }

    private static byte[] StripPngText(byte[] png)
    {
        var o = new MemoryStream();
        o.Write(png, 0, 8);
        int pos = 8;
        while (pos < png.Length)
        {
            int len = png[pos] << 24 | png[pos + 1] << 16 | png[pos + 2] << 8 | png[pos + 3];
            if (System.Text.Encoding.ASCII.GetString(png, pos + 4, 4) != "tEXt") o.Write(png, pos, 12 + len);
            pos += 12 + len;
        }
        return o.ToArray();
    }

    public void RunPsdTest(string dayPsd, string? nightPsd, string workDir, string reportPath)
    {
        var log = new List<string>();
        int fail = 0;
        void Check(string name, bool ok)
        {
            log.Add($"{(ok ? "OK  " : "FAIL")} {name}");
            if (!ok) fail++;
        }
        try
        {
            Directory.CreateDirectory(workDir);
            var panelPath = Path.Combine(workDir, "panel.txt");
            _autoConfirm = _ => true;
            var sw = Stopwatch.StartNew();
            using (var day = PsdFile.Open(dayPsd))
            using (var night = nightPsd != null ? PsdFile.Open(nightPsd) : null)
            {
                log.Add($"     昼 {Path.GetFileName(dayPsd)} {day.Width}x{day.Height} レイヤー {day.Layers.Count}" + (night != null ? $"／夜 {Path.GetFileName(nightPsd)} レイヤー {night.Layers.Count}" : ""));
                var spec = new PsdImportSpec
                {
                    Day = day, Night = night, PanelDir = workDir, OutputDir = Path.Combine(workDir, "psd"),
                    BaseLayers = day.PixelLayers.Where(l => PsdImport.DefaultRole(l, day.Canvas) == PsdRole.Base).Select(l => l.Path).Distinct().ToList(),
                    LampLayers = day.PixelLayers.Where(l => PsdImport.DefaultRole(l, day.Canvas) == PsdRole.Lamp).Select(l => l.Path).Distinct().ToList(),
                    Layer = 5,
                };
                log.Add($"     初期の役割: ベース {spec.BaseLayers.Count}・表示灯 {spec.LampLayers.Count}");
                Check("新しいパネルとして読み込める", ApplyPsdImport(spec, panelPath, interactive: false));
                log.Add($"     読み込みにかかった時間 {sw.Elapsed.TotalSeconds:0.0} 秒");
                var lamps = _doc!.Sections.Where(s => s.Kind == SectionKind.PilotLamp && !s.IsMainPanel).ToList();
                Check("表示灯ができる", lamps.Count > 0 && lamps.Count <= spec.LampLayers.Count);
                log.Add($"     表示灯 {lamps.Count} 個（キャンバスの外などで除いたもの {spec.LampLayers.Count - lamps.Count} 個）");
                string Full(string rel) => Path.GetFullPath(Path.Combine(workDir, rel));
                Check("ベースの画像を書き出し、メインパネルにする（[This] は画像なし）", _doc.MainPanel?.Get("DaytimeImage") is { } b && File.Exists(Full(b))
                    && _doc.MainPanel.PsdLayers.Count > 0 && _doc.This?.Get("DaytimeImage") == null);
                if (night != null)
                {
                    Check("夜のベースも書き出す", _doc.MainPanel?.Get("NighttimeImage") is { } bn && File.Exists(Full(bn)));
                    int withNight = lamps.Count(s => s.Get("NighttimeImage") is { } n && File.Exists(Full(n)));
                    Check("夜の画像のある表示灯がある", withNight > 0);
                    log.Add($"     夜の画像のある表示灯 {withNight} 個");
                }
                Check("全部の画像ファイルがある", _doc.Sections.All(s => s.Get("DaytimeImage") is not { Length: > 0 } d || File.Exists(Full(d))));
                Check("つながりのコメントが入る", Editor.Text.Contains(";===== PSD 昼:") && Editor.Text.Contains(";@PSDレイヤー:"));
                Check("問題一覧に Subject の空きが出る", _scene!.Problems.Any(p => p.Message.Contains("Subject")));
                Check("画像が読めない・ない問題はない", !_scene.Problems.Any(p => p.Message.Contains("画像")));
                log.Add($"     注意 {_lastPsdWarnings.Count} 件: " + string.Join(" / ", _lastPsdWarnings.Take(5)));
            }

            Check("保存できる", Save(_path));
            int count = _doc!.Sections.Count;
            LoadPanel(_path!, null);
            Check("開き直してもつながりが残る", _doc!.HasPsdLink && _doc.Sections.Count == count && _doc.PsdNight != null == (nightPsd != null));
            Check("開き直してすぐは「新しくなった」と出ない", !MessageText.Text.Contains("新しく"));

            // 夜のベースは同じ名前のレイヤーを全部重ねる（見本の夜の PSD には同じ名前のレイヤーが 2 枚ある）
            if (nightPsd != null)
            {
                using var nightCheck = PsdFile.Open(nightPsd);
                int dupNames = nightCheck.Layers.Count(l => l.Path.EndsWith(" #2"));
                log.Add($"     夜の PSD の同じ名前のレイヤー（#2 以降）: {dupNames} 個");
            }

            // 複製したオブジェクトは PSD とつながらない
            var linked = _doc.Sections.FindIndex(x => x.Kind == SectionKind.PilotLamp && x.PsdLayers.Count > 0);
            SelectSection(linked);
            OnDuplicate(null, null);
            Check("複製した表示灯は PSD とつながらない", _doc.Sections[_selected].PsdLayers.Count == 0 && _doc.Sections[linked].PsdLayers.Count > 0);
            OnUndo(this, null!);

            // 名前を付けて別のフォルダに保存すると PSD のパスも直る
            var sub = Path.Combine(workDir, "sub");
            Directory.CreateDirectory(sub);
            Check("別のフォルダに保存できる", SaveAsTo(Path.Combine(sub, "panel2.txt")));
            Check("PSD のパスが新しい場所から合っている", LinkedPsd(false) is { } dp && SamePath(dp, dayPsd));
            Check("画像のパスも合っている", !_scene!.Problems.Any(p => p.Message.Contains("画像")));

            Canvas.FitAll();
            UpdateLayout();
            SaveWindowPng(this, Path.ChangeExtension(reportPath, ".png"));
        }
        catch (Exception ex)
        {
            log.Add("EXCEPTION " + ex);
            fail++;
        }
        _autoConfirm = null;
        log.Add(fail == 0 ? "ALL PASSED" : $"{fail} FAILED");
        File.WriteAllLines(reportPath, log);
        _savedText = Editor.Text;
        Close();
    }
}
