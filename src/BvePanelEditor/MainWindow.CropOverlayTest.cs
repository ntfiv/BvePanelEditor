using System.IO;
using BvePanelEditor.Core;
using BvePanelEditor.Core.Crop;
using BvePanelEditor.Rendering;
using BvePanelEditor.Views;

namespace BvePanelEditor;

/// <summary>
/// 確認用: 画像からパネルを作るで、下に敷く・上に重ねる画像も使う（--cropoverlaytest 昼・無発光.png 作業フォルダ 結果.txt）。元の画像は読むだけ。
/// 背景の画像を下に敷き、gs.png をその上（1400, 0）、BVEPanel_door.png を「door」で上に重ねる。「重ねる画像」のタブの一覧・↑↓・ドラッグ・ドロップも確かめる。
/// </summary>
public partial class MainWindow
{
    public async void RunCropOverlayTest(string firstImage, string workDir, string reportPath)
    {
        var log = new List<string>();
        int fail = 0;
        void Check(string name, bool ok) { log.Add($"{(ok ? "OK  " : "FAIL")} {name}"); if (!ok) fail++; }
        try
        {
            Directory.CreateDirectory(workDir);
            _autoConfirm = _ => true;
            var dir = Path.GetDirectoryName(firstImage)!;
            // 見本のフォルダの背景の画像（名前が「…bg.png」のもの。2 枚）
            var bgs = Directory.GetFiles(dir, "*bg.png").Select(f => Path.GetFileNameWithoutExtension(f)).Order().ToList();
            string bgA = bgs[0], bgB = bgs[1];
            var w = new ImagePanelWindow(this, workDir, firstImage, 5);
            w.Show();
            await Task.Delay(1500);
            w.Detect(replace: true);
            Check("表示灯の名前は Lamp_01 から", w.Regions.Count > 0 && w.Regions[0].Name == "Lamp_01" && w.Regions.All(r => r.Name.StartsWith("Lamp_")));
            Check("次の名前は使っていない番号", w.NextNameForTest() == $"Lamp_{w.Regions.Count + 1:00}");
            // 囲みの一覧で Delete: 選んだ囲みを消す（Ctrl+Z で戻る）
            int n0 = w.Regions.Count;
            w.SelectForTest(w.Regions.Take(1).ToList());
            w.DeleteRegionsByKeyForTest();
            Check("囲みの一覧で Delete キーを押すと選んだ囲みを消す", w.Regions.Count == n0 - 1);
            w.UndoRegions();
            Check("Ctrl+Z で戻る", w.Regions.Count == n0);
            w.AddOverlayForTest(Path.Combine(dir, bgA + ".png"), below: true, 0, 0);
            w.AddOverlayForTest(Path.Combine(dir, "gs.png"), below: true, 0, 0);
            w.AddOverlayForTest(Path.Combine(dir, "BVEPanel_door.png"), below: false, 0, 0, subject: "door");

            // 「重ねる画像」のタブ: 重ね順の一覧（上ほど手前）
            w.ShowOverlayTabForTest();
            await Task.Delay(500);
            log.Add("     一覧: " + string.Join(" ／ ", w.OverlayRowsForTest));
            Check("重ね順の一覧: 扉 → 表示灯 → メインパネル → gs → 背景", w.OverlayRowsForTest.SequenceEqual(["BVEPanel_door", "表示灯", "メインパネル", "gs", bgA]));
            // ↑ でメインパネルをまたぐと上に重ねる、↓ で戻る
            w.SelectOverlayForTest("gs");
            w.MoveOverlay(-1);
            Check("↑ でメインパネル・表示灯をまたぐと上に重ねるになる", w.OverlayRowsForTest.SequenceEqual(["BVEPanel_door", "gs", "表示灯", "メインパネル", bgA]) && w.SelectedOverlayForTest?.Below == false);
            w.MoveOverlay(1);
            Check("↓ で下に敷くに戻る", w.OverlayRowsForTest.SequenceEqual(["BVEPanel_door", "表示灯", "メインパネル", "gs", bgA]) && w.SelectedOverlayForTest?.Below == true);
            w.SelectOverlayForTest(bgA);
            w.MoveOverlay(-1);
            Check("下に敷く画像どうしの入れ替え", w.OverlayRowsForTest.SequenceEqual(["BVEPanel_door", "表示灯", "メインパネル", bgA, "gs"]));
            w.MoveOverlay(1);
            // 見本の上でドラッグして gs を 1400, 0 へ
            w.SelectOverlayForTest("gs");
            Check("選ぶと見本に枠を出す", w.OverlayModeForTest);
            w.DragOverlayForTest(1400, 0);
            Check("ドラッグで位置が変わり、設定の欄にも出る", w.SelectedOverlayForTest is { X: 1400, Y: 0 } && w.OverlayPropsForTest == ("1400", "0"));
            w.MoveSelectedOverlay(10, -10);
            w.MoveSelectedOverlay(-10, 10);
            Check("矢印キーで動かす", w.SelectedOverlayForTest is { X: 1400, Y: 0 });
            await Task.Delay(300);
            SaveWindowPng(w, Path.ChangeExtension(reportPath, ".window.png"));
            // いちばん下の画像も X・Y のとおりに置く（重ねた見本が変わる）
            w.SelectOverlayForTest(bgA);
            var s0 = w.StackedForTest(false);
            w.SetOverlayXForTest("40");
            var s1 = w.StackedForTest(false);
            Check("いちばん下の画像も位置どおりに見本に出る（ずらすと見本が変わる）", !s0.Bgra.AsSpan().SequenceEqual(s1.Bgra));
            w.SetOverlayXForTest("0");
            // 一覧へのドロップ: gs の行に落とすと、下に敷く・gs のすぐ手前
            w.DropOverlayFiles([Path.Combine(dir, bgB + ".png")], below: true, onto: null);
            Check("ドロップで足す（下に敷く・選んだ状態）", w.OverlayRowsForTest.Contains(bgB) && w.SelectedOverlayForTest?.Name == bgB && w.SelectedOverlayForTest.Below);
            w.RemoveSelectedOverlayForTest();
            w.DropOverlayFiles([Path.Combine(dir, bgB + ".png")], below: false, onto: null);
            Check("メインパネルより上に落とすと上に重ねる（いちばん手前）", w.OverlayRowsForTest[0] == bgB && w.SelectedOverlayForTest?.Below == false);
            w.RemoveSelectedOverlayForTest();
            Check("消すと一覧から外れる", w.OverlayRowsForTest.SequenceEqual(["BVEPanel_door", "表示灯", "メインパネル", "gs", bgA]));

            // 扉違いの画像を「部品用」に足す（下の一覧）
            w.AddOverlayFiles([Path.Combine(dir, "BVEPanel_door.png")], below: true, after: null, crop: true);
            w.RenameSelectedOverlayForTest("扉");
            w.SetOverlayNightForTest(Path.Combine(dir, "BVEPanelN_door.png"));
            log.Add("     一覧: " + string.Join(" ／ ", w.OverlayRowsForTest));
            log.Add("     部品用: " + string.Join(" ／ ", w.PartsRowsForTest));
            Check("部品用の画像は下の一覧に出て、上の一覧には出ない", w.PartsRowsForTest.SequenceEqual(["扉"]) && !w.OverlayRowsForTest.Contains("扉")
                && w.SelectedIsCropSourceForTest && w.SelectedOverlayForTest is { X: 0, Y: 0 });
            // 上の一覧で選ぶと下の選択は外れ、下で選ぶと上が外れる（設定の欄は 1 つ）
            w.SelectOverlayForTest("gs");
            Check("上の一覧で選ぶと、上の画像の設定が出る", w.SelectedOverlayForTest?.Name == "gs" && !w.SelectedIsCropSourceForTest);
            w.SelectOverlayForTest("扉");
            Check("下の一覧で選ぶと、上の選択が外れて部品用の設定が出る", w.SelectedOverlayForTest?.Name == "扉" && w.SelectedIsCropSourceForTest && w.OverlaySelectedRowForTest == null);
            Check("「部品を探す」の基準に「扉（扉・夜 − 夜・無発光）」が出る", w.BasisLabelsForTest.Contains("扉（扉・夜 − 夜・無発光）") && w.BasisLabelsForTest.Contains("扉（扉・昼 − 昼・無発光）"));
            int lit = w.Regions.Count;
            w.SelectBasisForTest("扉（扉・夜");
            int doors = w.Detect(replace: false);
            log.Add($"     扉の基準で追加で探した囲み: {doors} 個");
            Check("扉の基準で追加で探すと、切り抜き元が「扉」の囲みが足される", doors > 0 && w.Regions.Count(r => r.Set == "扉") == doors && w.Regions.Count == lit + doors);
            // 単純な変化の差で探す（部品用のときだけ使える）
            w.SelectBasisForTest("夜（");
            Check("基準が発光のときは「単純な変化の差」は使えない", !w.SimpleDiffEnabledForTest);
            w.SelectBasisForTest("扉（扉・夜");
            Check("基準が部品用のときは「単純な変化の差」を使える", w.SimpleDiffEnabledForTest);
            var doorRegions = w.Regions.Where(r => r.Set == "扉").Select(r => r.Rect).ToList();
            // 発光の画像から作った部品用の画像（表示灯が点いたまま扉だけ違う）
            {
                var u = ImageLoader.Load(Path.Combine(dir, "BVEPanel_N.png")).Pixels;
                var l = ImageLoader.Load(Path.Combine(dir, "BVEPanel_N_TT.png")).Pixels;
                var dn = ImageLoader.Load(Path.Combine(dir, "BVEPanelN_door.png")).Pixels;
                var bgra = (byte[])l.Bgra.Clone();
                for (int i = 0; i + 3 < bgra.Length && i + 3 < dn.Bgra.Length; i += 4)
                    if (Math.Abs(dn.Bgra[i] - u.Bgra[i]) + Math.Abs(dn.Bgra[i + 1] - u.Bgra[i + 1]) + Math.Abs(dn.Bgra[i + 2] - u.Bgra[i + 2]) > 12)
                        for (int c = 0; c < 4; c++) bgra[i + c] = dn.Bgra[i + c];
                var litDoor = Path.Combine(workDir, "door_lit_N.png");
                File.WriteAllBytes(litDoor, Core.Psd.PngWriter.Encode(l.Width, l.Height, bgra));
                w.AddOverlayFiles([Path.Combine(dir, "BVEPanel_door.png")], below: true, after: null, crop: true);
                w.RenameSelectedOverlayForTest("扉点灯");
                w.SetOverlayNightForTest(litDoor);
            }
            int baseCount = w.Regions.Count;
            w.SelectBasisForTest("扉点灯（扉点灯・夜");
            w.SetSimpleDiffForTest(false);
            int autoLit = w.Detect(replace: true);
            w.UndoRegions();
            w.SetSimpleDiffForTest(true);
            int simpleLit = w.Detect(replace: true);
            var simpleRects = w.Regions.Select(r => r.Rect).ToList();
            w.ShowRegionTabForTest();
            await Task.Delay(400);
            SaveWindowPng(w, Path.ChangeExtension(reportPath, ".simple.png"));
            w.ShowOverlayTabForTest();
            log.Add($"     発光から作った部品用の画像: 自動 {autoLit} 個・単純な変化の差 {simpleLit} 個（扉の画像の自動では {doors} 個） {w.DetectNoteForTest}");
            Check("単純な変化の差では、発光の画像と比べて扉の所だけを囲む（表示灯を拾わない）", simpleLit > 0 && simpleLit < autoLit
                && w.DetectNoteForTest.Contains("発光の画像と比べました") && simpleRects.All(r => doorRegions.Any(d => !r.Intersect(d).IsEmpty)));
            w.UndoRegions();
            w.SelectBasisForTest("扉（扉・夜");
            int simpleUnlit = w.Detect(replace: true);
            Check("無発光から作った部品用の画像では、無発光と比べる", simpleUnlit > 0 && w.DetectNoteForTest.Contains("無発光の画像と比べました"));
            w.UndoRegions();
            w.SetSimpleDiffForTest(false);
            w.SelectOverlayForTest("扉点灯");
            w.RemoveSelectedOverlayForTest();
            Check("元に戻る", w.Regions.Count == baseCount && w.Regions.Count(r => r.Set == "扉") == doors && w.PartsRowsForTest.SequenceEqual(["扉"]));

            // 部品用の一覧で Delete: 外す（使っていた囲みは「発光」に戻る）。別の画像で確かめる
            w.AddOverlayFiles([Path.Combine(dir, "BVEPanel_door.png")], below: true, after: null, crop: true);
            w.RenameSelectedOverlayForTest("仮");
            Check("部品用は 2 つ", w.PartsRowsForTest.SequenceEqual(["扉", "仮"]));
            w.RemoveSelectedOverlayByKeyForTest();
            Check("部品用の一覧で Delete キーを押すと外れる（ほかは残る）", w.PartsRowsForTest.SequenceEqual(["扉"]) && w.Regions.Count(r => r.Set == "扉") == doors);
            // 透明な所のある画像（扉の所だけ残したレイヤー）を切り抜く元にする
            static int Clear(Core.Psd.PsdPixels p) { int n = 0; for (int i = 3; i < p.Bgra.Length; i += 4) if (p.Bgra[i] != 255) n++; return n; }
            string LayerOf(string doorName, string unlitName, string outName)
            {
                var unlitPx = ImageLoader.Load(Path.Combine(dir, unlitName)).Pixels;
                var doorPx = ImageLoader.Load(Path.Combine(dir, doorName)).Pixels;
                var bgra = (byte[])doorPx.Bgra.Clone();
                for (int i = 0; i < bgra.Length; i += 4)
                    if (i + 3 < unlitPx.Bgra.Length && Math.Abs(bgra[i] - unlitPx.Bgra[i]) + Math.Abs(bgra[i + 1] - unlitPx.Bgra[i + 1]) + Math.Abs(bgra[i + 2] - unlitPx.Bgra[i + 2]) + Math.Abs(bgra[i + 3] - unlitPx.Bgra[i + 3]) < 4)
                        bgra[i] = bgra[i + 1] = bgra[i + 2] = bgra[i + 3] = 0;
                var path = Path.Combine(workDir, outName);
                File.WriteAllBytes(path, Core.Psd.PngWriter.Encode(doorPx.Width, doorPx.Height, bgra));
                return path;
            }
            var mainClear = Clear(ImageLoader.Load(firstImage).Pixels);
            w.AddOverlayFiles([LayerOf("BVEPanel_door.png", "BVEPanel.png", "door_layer.png")], below: true, after: null, crop: true);
            w.RenameSelectedOverlayForTest("扉レイヤー");
            w.SetOverlayNightForTest(LayerOf("BVEPanelN_door.png", "BVEPanel_N.png", "door_layer_N.png"));
            Check("「そのレイヤーのみを切り抜く」は切り抜く元のときだけ使えて、最初は外れている", w.LayerOnlyEnabledForTest && !w.Sets("扉レイヤー").LayerOnly);
            int before = w.Regions.Count;
            w.SelectBasisForTest("扉（扉・夜");
            int doorsAll = w.Detect(replace: true);
            var doorRects = w.Regions.Select(r => r.Rect).ToList();
            w.UndoRegions();
            w.SelectBasisForTest("扉レイヤー（扉レイヤー・夜");
            int layerDoors = w.Detect(replace: true);
            log.Add($"     透明なレイヤーの基準で探した囲み: {layerDoors} 個（扉の画像では {doorsAll} 個）");
            Check("透明なレイヤーでも、透明な所は差に出ずに、扉の画像で見つかる所だけが見つかる", layerDoors > 0 && w.Regions.All(r => r.Set == "扉レイヤー")
                && w.Regions.All(r => doorRects.Any(d => !r.Rect.Intersect(d).IsEmpty)));
            w.UndoRegions();
            // 扉の囲みの 1 つを、透明なレイヤーから切り抜くようにする
            w.SelectForTest(w.Regions.Where(r => r.Set == "扉").Take(1).ToList());
            w.SetSelectedSetForTest("扉レイヤー");
            var sOff = w.MakeSpecForTest()!.Sets.First(x => x.Name == "扉レイヤー");
            Check("外したとき: 透明な所へメインパネルを重ねて切り抜く（メインパネルに重ねる）", sOff.OverMain && Clear(sOff.Day) <= mainClear && sOff.Night != null);
            w.SetLayerOnlyForTest(true);
            var sOn = w.MakeSpecForTest()!.Sets.First(x => x.Name == "扉レイヤー");
            Check("付けたとき: そのレイヤーのみ（透明な所は透明のまま）", !sOn.OverMain && Clear(sOn.Day) > mainClear && w.Sets("扉レイヤー").LayerOnly);
            w.SetLayerOnlyForTest(false);
            await Task.Delay(300);
            SaveWindowPng(w, Path.ChangeExtension(reportPath, ".crop.png"));
            w.SelectBasisForTest("夜（");

            // 状態量のない囲みがあれば、作る前に常表示（true）にするか聞く
            int emptyBefore = w.Regions.Count(r => r.Subject.Length == 0);
            string? asked = null;
            Views.ChoiceDialog.TestAnswer = (msg, items) => { asked = msg + "\n" + string.Join("\n", items); return 2; };
            Check("状態量のない囲みがあると聞き、「やめる」なら作らない（何も変えない）", emptyBefore > 0 && !w.AskEmptySubjectsForTest() && asked != null
                && asked.Contains($"{emptyBefore} 個") && w.Regions.Count(r => r.Subject.Length == 0) == emptyBefore);
            Views.ChoiceDialog.TestAnswer = (_, _) => 1;
            Check("「ats99 に仮設定」なら状態量のなかった囲みを ats99 にする", w.AskEmptySubjectsForTest() && w.Regions.All(r => r.Subject.Length > 0)
                && w.Regions.Count(r => r.Subject == Views.ImagePanelWindow.TemporarySubject) >= emptyBefore && asked!.Contains("ats99"));
            w.UndoRegions();
            Check("Ctrl+Z で状態量なしに戻る（ats99）", w.Regions.Count(r => r.Subject.Length == 0) == emptyBefore);
            Views.ChoiceDialog.TestAnswer = (_, _) => 0;
            Check("「常表示にして作る」なら true を入れる", w.AskEmptySubjectsForTest() && w.Regions.All(r => r.Subject.Length > 0)
                && w.Regions.Count(r => r.Subject == "true") >= emptyBefore);
            w.UndoRegions();
            Check("Ctrl+Z で状態量なしに戻る", w.Regions.Count(r => r.Subject.Length == 0) == emptyBefore);
            Views.ChoiceDialog.TestAnswer = null;
            log.Add($"     状態量のない囲み: {emptyBefore} 個");
            // 選ぶ画面の見た目（長い選択肢が折り返されるか）
            Views.ChoiceDialog.SnapshotPath = Path.ChangeExtension(reportPath, ".ask.png");
            w.AskEmptySubjectsForTest();
            Views.ChoiceDialog.SnapshotPath = null;
            Check("選ぶ画面を開いて閉じても、状態量は変わらない", w.Regions.Count(r => r.Subject.Length == 0) == emptyBefore);

            var spec = w.MakeSpecForTest()!;
            Check("切り抜く元は切り抜き元の組になり、重ねる画像には入らない", spec.Sets.Any(x => x.Name == "扉" && x.Night != null) && spec.Overlays.All(o => o.Name != "扉"));
            w.Close();

            var panelPath = Path.Combine(workDir, "panel.txt");
            Check("新しいパネルを作れる", ApplyCropBuild(spec, panelPath, interactive: false));
            string Full(string rel) => Path.GetFullPath(Path.Combine(workDir, rel));
            Check("[This] は画像なし（透明な画像は書き出さない）", _doc!.This?.Get("DaytimeImage") == null && !Directory.EnumerateFiles(PanelDir!, "this_blank_*").Any());
            var secs = _doc.Sections.Where(s => s.Kind == SectionKind.PilotLamp).ToList();
            var bg = secs.FirstOrDefault(s => s.Title == bgA);
            Check("下に敷いた背景の画像は常表示の表示灯・レイヤー 1", bg?.Get("Subject") == "true" && bg.Get("Layer") == "1" && bg.Get("DaytimeImage") is { } bd && Full(bd) == Path.GetFullPath(Path.Combine(dir, bgA + ".png")));
            var gs = secs.FirstOrDefault(s => s.Title == "gs");
            var body = _doc.MainPanel;
            var door = secs.FirstOrDefault(s => s.Title == "BVEPanel_door");
            Check("gs は常表示・レイヤー 2", gs?.Get("Subject") == "true" && gs.Get("Layer") == "2");
            Check("メインパネル（4 点の画像）は常表示・0,0・レイヤー 3・昼夜あり", body?.Get("Subject") == "true" && body.Get("Location") == "0, 0" && body.Get("Layer") == "3"
                && body.Get("NighttimeImage") is { Length: > 0 });
            var lamps = secs.Where(s => s.CropSet == "発光").ToList();
            Check("切り抜いた表示灯はメインパネルより上（レイヤー 4 以上）", lamps.Count > 0 && lamps.All(s => PanelValues.Double(s.Get("Layer"), 0) >= 4));
            int maxLamp = lamps.Max(s => (int)PanelValues.Double(s.Get("Layer"), 0));
            Check("上に重ねた扉は door・表示灯より上", door?.Get("Subject") == "door" && PanelValues.Double(door.Get("Layer"), 0) > maxLamp);
            Check("画像の問題はない", !_scene!.Problems.Any(p => p.Message.Contains("画像")));
            Check("パネルに扉の切り抜き元と、扉から切り抜いた表示灯が入る", Editor.Text.Contains(";===== 切り抜き元 扉: ") && Editor.Text.Contains(";@切り抜き: 扉")
                && secs.Count(s => s.CropSet == "扉") > 0 && secs.Where(s => s.CropSet == "扉").All(s => s.Get("NighttimeImage") is { Length: > 0 }));
            log.Add($"     計器: {string.Join("、", secs.Take(4).Select(s => $"{s.Title}(L{s.Get("Layer")})"))} …　ほか、最後 {secs[^1].Title}(L{secs[^1].Get("Layer")})");

            Check("保存できる", Save(_path));
            int count = _doc.Sections.Count;
            var text = Editor.Text;
            LoadPanel(_path!, null);
            Check("開き直しても同じ", _doc!.Sections.Count == count && Editor.Text == text);
            var layerLamp = _doc!.Sections.FirstOrDefault(s => s.CropSet == "扉レイヤー");
            var lampFile = layerLamp?.Get("DaytimeImage") is { } lf ? Full(lf) : "";
            var lampBytes = File.Exists(lampFile) ? File.ReadAllBytes(lampFile) : [];
            var plan = RunCropUpdate(interactive: false);
            Check("切り抜き元から更新しても下に敷いた画像・元の画像は触らない", plan != null && plan.Missing.Count == 0
                && plan.Files.All(f => f.FullPath.StartsWith(Path.GetFullPath(workDir), StringComparison.OrdinalIgnoreCase)));
            Check("すぐ更新してもパネルは変わらない", Editor.Text == text);
            Check("パネルに「メインパネルに重ねる」が書かれる", Editor.Text.Contains(";===== 切り抜き元 扉レイヤー: 昼=") && Editor.Text.Contains(" | メインパネルに重ねる ====="));
            Check("切り抜き元から更新しても、メインパネルに重ねた同じ絵になる", layerLamp != null && lampBytes.Length > 0 && plan!.Files.Any(f => string.Equals(f.FullPath, lampFile, StringComparison.OrdinalIgnoreCase)
                && lampBytes.AsSpan().SequenceEqual(f.Png)));

            NightSlider.Value = 0;
            AllLampsCheck.IsChecked = true;
            OnPreviewOption(this, new System.Windows.RoutedEventArgs());
            Canvas.FitAll();
            UpdateLayout();
            await Task.Delay(500);
            SaveWindowPng(this, Path.ChangeExtension(reportPath, ".png"));
        }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); fail++; }
        _autoConfirm = null;
        log.Add(fail == 0 ? "ALL PASSED" : $"{fail} FAILED");
        File.WriteAllLines(reportPath, log);
        _savedText = Editor.Text;
        Close();
    }
}
