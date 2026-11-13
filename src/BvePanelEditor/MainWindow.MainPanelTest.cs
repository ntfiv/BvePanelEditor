using System.IO;
using System.Windows;
using System.Windows.Media;
using BvePanelEditor.Core;

namespace BvePanelEditor;

/// <summary>
/// 確認用: メインパネル（--mainpaneltest 2つ目のパネル 画面.png 結果.txt）。開いたパネル（[This] にパネルの画像があるもの）と
/// 2 つ目のパネル（表示灯にパネル本体があるもの）のコピーに対して使う（保存する）。
/// </summary>
public partial class MainWindow
{
    private static void SaveGrab((byte[] Px, int W, int H) g, string path)
    {
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(g.W, g.H, 96, 96, PixelFormats.Pbgra32, null, g.Px, g.W * 4);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        enc.Save(f);
    }

    public async void RunMainPanelTest(string second, string png, string report)
    {
        var log = new List<string>();
        int fail = 0;
        void Check(string name, bool ok) { log.Add($"{(ok ? "OK  " : "FAIL")} {name}"); if (!ok) fail++; }
        double Diff((byte[] Px, int W, int H) a, (byte[] Px, int W, int H) b)
        {
            int n = 0;
            for (int i = 0; i < a.Px.Length && i < b.Px.Length; i += 4)
                if (Math.Abs(a.Px[i] - b.Px[i]) > 8 || Math.Abs(a.Px[i + 1] - b.Px[i + 1]) > 8 || Math.Abs(a.Px[i + 2] - b.Px[i + 2]) > 8) n++;
            return n / (double)(a.Px.Length / 4);
        }
        try
        {
            // ---- 1. [This] の画像をメインパネルにする（[This] にパネルの画像があるパネル） ----
            var doc = _doc!;
            ShowFrameMenu.IsChecked = false;
            OnToggleFrame(ShowFrameMenu, new RoutedEventArgs());
            SelectForStartup(-1);
            Canvas.FitAll();
            UpdateLayout();
            await Task.Delay(600);
            var before = Grab();
            var vlBefore = OpenViewLimit(Path.ChangeExtension(png, ".vl1.png"));
            var thisImage = doc.This!.Get("DaytimeImage");
            var layersBefore = doc.Sections.Where(s => s.Kind is not (SectionKind.This or SectionKind.Unknown)).Select(s => (int)PanelValues.Double(s.Get("Layer"), 0)).ToList();
            Check("[This] の画像をメインパネルにできる", ThisToMainPanel());
            doc = _doc!;
            var main = doc.MainPanel;
            Check("メインパネルは [This] の画像・レイヤー 1・常表示", main != null && main.Get("DaytimeImage") == thisImage && main.Get("Layer") == "1" && main.Get("Subject") == "true");
            Check("[This] からは画像を外す（透明な画像は書き出さない）", doc.This!.Get("DaytimeImage") == null && doc.This.Get("NighttimeImage") == null
                && !Directory.EnumerateFiles(PanelDir!, "this_blank_*").Any());
            var others = doc.Sections.Where(s => s.Kind is not (SectionKind.This or SectionKind.Unknown) && !s.IsMainPanel).Select(s => (int)PanelValues.Double(s.Get("Layer"), 0)).ToList();
            log.Add($"     レイヤー: 前 {string.Join(",", layersBefore)} → 後 {string.Join(",", others)}");
            Check("ほかのオブジェクトはメインパネルより上（同じだけ上げる）", others.All(l => l >= 2) && others.Zip(layersBefore).Select(t => t.First - t.Second).Distinct().Count() == 1);
            Check("透過色もメインパネルに移す", main?.Get("TransparentColor") == "000000" && doc.This.Get("TransparentColor") == null);

            SelectForStartup(-1); // 選んだ枠を描かずに比べる
            UpdateLayout();
            await Task.Delay(600);
            var after = Grab();
            SaveGrab(before, Path.ChangeExtension(png, ".before.png"));
            SaveGrab(after, Path.ChangeExtension(png, ".after.png"));
            double d1 = Diff(before, after);
            log.Add($"     見た目の違い（画面の点の割合）: {d1:P2}");
            Check("見た目は変わらない（違う点 0.5% 未満）", d1 < 0.005);

            var row = (ElementList.ItemsSource as IEnumerable<ElementRow>)!.First(r => r.Index == doc.Sections.IndexOf(main!));
            Check("一覧は「メインパネル」・色は赤", row.Primary == "メインパネル" && row.KindBrush == Brushes.Red);
            var thisRow = (ElementList.ItemsSource as IEnumerable<ElementRow>)!.First(r => r.Index == doc.Sections.IndexOf(doc.This));
            Check("[This] の行は「[This]（画像なし）」", thisRow.Kind == "[This]" && thisRow.Subject == "（画像なし）");

            var vlAfter = OpenViewLimit(Path.ChangeExtension(png, ".vl2.png"));
            log.Add($"     表示範囲（前）: {vlBefore?.Split('\n')[0]}");
            log.Add($"     表示範囲（後）: {vlAfter?.Split('\n')[0]}");
            Check("表示範囲の計算はメインパネルの画像から（前と同じ結果）", vlAfter != null && vlAfter == vlBefore);

            // パネル画面のクリック: メインパネルは選ばず、オブジェクトは選べる
            var needle = _scene!.Items.First(i => i.Kind == SectionKind.Needle);
            Check("メインパネルの上をクリックしても選ばない（[This] を選ぶ）", Canvas.HitForTest(new Point(200, 2600)) == -1);
            Check("オブジェクトはクリックで選べる", Canvas.HitForTest(needle.Location) is var h && h >= 0 && !doc.Sections[h].IsMainPanel);
            SelectAllElements(null, null);
            Check("すべて選択でもメインパネルは選ばない", !_selSet.Contains(doc.Sections.IndexOf(main!)));

            OnResolutionFromMain();
            var mw = Core.Psd.PngWriter.TryReadContentSize(Path.Combine(PanelDir!, main!.Get("DaytimeImage")!), out var cw, out _) ? cw
                : Rendering.ImageLoader.TryLoadPixels(Path.Combine(PanelDir!, main.Get("DaytimeImage")!))!.Width;
            Check($"解像度をメインパネルの幅（{mw}）にする", doc.This.Get("Resolution") == mw.ToString());

            Check("保存できる", Save(_path));
            var text = Editor.Text;
            LoadPanel(_path!, null);
            Check("開き直してもメインパネルと画像のない [This] のまま", _doc!.MainPanel != null && IsBlankThis() && Editor.Text == text && text.Contains(";@メインパネル"));
            SelectSection(doc.Sections.IndexOf(main));
            UpdateLayout();
            await Task.Delay(400);
            SaveWindowPng(this, png);
            SelectSection(0);
            UpdateLayout();
            await Task.Delay(300);
            SaveWindowPng(this, Path.ChangeExtension(png, ".this.png"));

            // ---- 2. 表示灯をメインパネルにする（2 つ目のパネル: [This] は背景、パネル本体は _base\Panel.png の表示灯） ----
            LoadPanel(second, null);
            doc = _doc!;
            SelectForStartup(-1);
            Canvas.FitAll();
            UpdateLayout();
            await Task.Delay(600);
            var b2 = Grab();
            int idx = doc.Sections.FindIndex(s => s.Kind == SectionKind.PilotLamp && (s.Get("DaytimeImage") ?? "").EndsWith(Path.Combine("_base", "Panel.png")));
            var bgImage = doc.This!.Get("DaytimeImage");
            Check("表示灯をメインパネルにできる（[This] の画像は表示灯に移す）", idx >= 0 && MakeMainPanel(idx, moveThisImage: true));
            doc = _doc!;
            var moved = doc.Sections.FirstOrDefault(s => s.Kind == SectionKind.PilotLamp && s.Get("DaytimeImage") == bgImage);
            log.Add($"     移した背景: {moved?.Title} L{moved?.Get("Layer")}・メインパネル L{doc.MainPanel?.Get("Layer")}");
            Check("背景は常表示の表示灯・いちばん下（レイヤー 0）", moved != null && moved.Get("Subject") == "true" && moved.Get("Layer") == "0" && !moved.IsMainPanel);
            Check("[This] は画像なし・メインパネルは _base\\Panel.png", IsBlankThis() && doc.This!.Get("DaytimeImage") == null && doc.MainPanel?.Get("DaytimeImage")?.EndsWith(Path.Combine("_base", "Panel.png")) == true);
            SelectForStartup(-1);
            UpdateLayout();
            await Task.Delay(600);
            double d2 = Diff(b2, Grab());
            log.Add($"     見た目の違い: {d2:P2}");
            Check("見た目は変わらない（違う点 0.5% 未満）", d2 < 0.005);
            Check("保存できる（2 つ目のパネル）", Save(_path));
            SaveWindowPng(this, Path.ChangeExtension(png, ".ct.png"));
        }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); fail++; }
        log.Add(fail == 0 ? "ALL PASSED" : $"{fail} FAILED");
        File.WriteAllLines(report, log);
        _savedText = Editor.Text;
        Close();
    }
}
