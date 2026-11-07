using System.IO;
using System.Windows;
using BvePanelEditor.Core;
using BvePanelEditor.Rendering;
using BvePanelEditor.Views;

namespace BvePanelEditor;

/// <summary>確認用: 標準の針を置く（--needletest 画面.png 結果.txt。開いたパネルのコピーに対して使う。保存はしない）。</summary>
public partial class MainWindow
{
    /// <summary>針の一覧の設定ファイル（exe フォルダの needles.txt。確認用の起動では一時フォルダ）。</summary>
    private async Task NeedleListEditTest(Action<string, bool> Check, List<string> log)
    {
        Check("確認用の起動では、設定ファイルを一時フォルダに置く", NeedleCatalog.StoreDir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase));
        var builtin = NeedleCatalog.Builtin();
        var labels0 = builtin.Select(p => p.Label).ToList();
        Check("書いて読み直すと標準の針と同じ（名前・画像・値）", NeedleCatalog.Parse(NeedleCatalog.Format(builtin)) is var (back, warn0) && warn0.Count == 0
            && back.Count == builtin.Count && back.Zip(builtin).All(t => t.First.Label == t.Second.Label && t.First.Note == t.Second.Note
                && t.First.Needles.Zip(t.Second.Needles).All(n => n.First.DayFile == n.Second.DayFile && n.First.NightFile == n.Second.NightFile
                    && n.First.OriginX == n.Second.OriginX && n.First.OriginY == n.Second.OriginY && n.First.Radius == n.Second.Radius
                    && n.First.Subject == n.Second.Subject && n.First.NaturalFreq == n.Second.NaturalFreq && n.First.Title == n.Second.Title)));

        // 前の版（標準の針の版 1）の設定ファイル: user の行・並びを残したまま、新しい標準の針を末尾に足す
        var old = new List<string> { "; 自分のコメント", "[一覧]", "名前 = 自分の針", "[針]", "DaytimeImage = nd_speed.png" };
        Check("古い設定ファイルには新しい標準の針を書き足す", NeedleCatalog.Upgrade(old) && old[0] == "; 自分のコメント"
            && NeedleCatalog.Parse(old).Presets.Select(p => p.Label).SequenceEqual(new[] { "自分の針" }.Concat(builtin.Where(b => b.Since == 2).Select(b => b.Label)))
            && !NeedleCatalog.Upgrade(old));
        log.Add("     版 2 で足した標準の針: " + string.Join("、", builtin.Where(b => b.Since == 2).Select(b => b.Label)));

        var w = new NeedlePickerWindow(this);
        w.Show();
        await Task.Delay(300);
        Check("設定ファイルがなければ標準の針で作る（needles.txt）", File.Exists(NeedleCatalog.ConfigFile) && w.LabelsForTest.SequenceEqual(labels0));
        var text = File.ReadAllText(NeedleCatalog.ConfigFile);
        Check("メモ帳で読める形（[一覧]・[針]・DaytimeImage = nd_speed.png）", text.Contains("[一覧]\r\n名前 = 速度計の針Ａタイプ") && text.Contains("DaytimeImage = nd_speed.png") && text.Contains("NaturalFreq = 6.3"));

        // ↑ で並べ替えると、ファイルにも入る
        w.SelectPresetForTest("時計（");
        int before = w.SelectedRowForTest;
        w.MoveSelected(-1);
        Check("↑ で 1 つ上へ・設定ファイルも書き直す", w.LabelsForTest[before - 1].StartsWith("時計（") && NeedleCatalog.Presets()[before - 1].Label.StartsWith("時計（"));

        // 手で書き足す（組・フルパスの画像・書き間違い）
        var tmp = Path.Combine(Path.GetTempPath(), "panelkun_my_needle_" + Environment.ProcessId + ".png");
        File.Copy(Path.Combine(NeedleCatalog.Folder, "nd_prs_up.png"), tmp, true);
        File.AppendAllLines(NeedleCatalog.ConfigFile,
        [
            "", "[一覧]", "名前 = 速度計と黒   ; コメントも書ける", "説明 = 手で足した組",
            "[針]", "名前 = 速度計", "DaytimeImage = nd_speed.png", "Origin = 16, 225", "Radius = 70", "Subject = kmph", "Maximum = 120",
            "[針]", "名前 = 黒", "DaytimeImage = nd_prs_up.png", "NighttimeImage = nd_prs_up_dark.png", "Origin = 32, 164", "Radius = abc", "Subject = bc",
            "", "[一覧]", "名前 = 自作の針", "[針]", "DaytimeImage = " + tmp, "Origin = 32, 164",
            "", "[一覧]", "名前 = 画像なし", "[針]", "DaytimeImage = nothing.png",
            "[変な見出し]",
        ], new System.Text.UTF8Encoding(true));
        w.ReloadConfig();
        var labels = w.LabelsForTest;
        log.Add("     読み直した一覧: " + string.Join("、", labels));
        log.Add("     お知らせ: " + w.ListWarningsForTest);
        Check("書き足した行が一覧に出る（並べ替えも残る）", labels.Contains("速度計と黒") && labels.Contains("自作の針") && labels[before - 1].StartsWith("時計（"));
        Check("書き間違い（数でない値・使えない見出し・画像なし）を知らせる", w.ListWarningsForTest.Contains("Radius") && w.ListWarningsForTest.Contains("変な見出し") && w.ListWarningsForTest.Contains("nothing.png"));
        w.SelectPresetForTest("速度計と黒");
        var set = w.CurrentForTest;
        Check("[針] 2 つで組になり、値が入る（書いていない値は既定）", set.Count == 2 && set[0].Radius == 70 && set[0].Maximum == 120 && set[1].Subject == "bc"
            && set[1].NightFile == Path.Combine(NeedleCatalog.Folder, "nd_prs_up_dark.png") && set[1].Radius == 60);
        Check("組の名前は一覧の名前（コメントは外す）", w.PresetNameForTest == "速度計と黒");
        w.SelectPresetForTest("自作の針");
        Check("フルパスの画像も使える", w.CurrentForTest[0].DayFile == tmp);
        await Task.Delay(300);
        SaveWindowPng(w, Path.Combine(Path.GetTempPath(), "panelkun_needle_list.png"));
        w.Close();
        File.Delete(tmp);

        // ファイルを消すと標準の針で作り直す
        File.Delete(NeedleCatalog.ConfigFile);
        Check("設定ファイルを消すと標準の針に戻る", NeedleCatalog.Presets().Select(p => p.Label).SequenceEqual(labels0) && File.Exists(NeedleCatalog.ConfigFile));
    }

    public async void RunNeedleTest(string png, string report)
    {
        var log = new List<string>();
        int fail = 0;
        void Check(string name, bool ok) { log.Add($"{(ok ? "OK  " : "FAIL")} {name}"); if (!ok) fail++; }
        try
        {
            var presets = NeedleCatalog.Presets();
            Check("標準の針がすべて exe の needles・needle_E217 フォルダにある", presets.SelectMany(p => p.Needles).All(n => File.Exists(n.DayFile) && File.Exists(n.NightFile!)));
            Check("E217 の針は needle_E217 フォルダ、needles には CC0 の針だけ", presets.SelectMany(p => p.Needles).Where(n => n.DayFile.Contains("_E217")).All(n => Path.GetDirectoryName(n.DayFile) == NeedleCatalog.E217Folder)
                && !Directory.EnumerateFiles(NeedleCatalog.Folder, "*_E217.png").Any() && File.Exists(Path.Combine(NeedleCatalog.E217Folder, "Readme.txt")));
            // 前の版の needles.txt（E217 の針を needles からの場所で書いたもの）も読める
            var (oldList, _) = NeedleCatalog.Parse(["[一覧]", "名前 = 古い E217", "[針]", "名前 = 針", "DaytimeImage = needle_speed_E217.png", "NighttimeImage = needle_speed_n_E217.png", "Origin = 8, 256"]);
            Check("前の版の needles.txt の E217 の針（needles からの場所）は needle_E217 から読む", oldList.Count == 1 && File.Exists(oldList[0].Needles[0].DayFile) && File.Exists(oldList[0].Needles[0].NightFile!));
            var (oldRedB, _) = NeedleCatalog.Parse(["[一覧]", "名前 = 古い赤Ｂ", "[針]", "名前 = 針", "DaytimeImage = nd_prs2_low.png", "NighttimeImage = nd_prs2_low_dark.png"]);
            Check("前の版の赤Ｂタイプ（nd_prs2_low）は needle_E217 の同じ画像を使う", oldRedB.Count == 1 && Path.GetFileName(oldRedB[0].Needles[0].DayFile) == "needle_prs_low_E217.png"
                && File.Exists(oldRedB[0].Needles[0].DayFile) && Path.GetFileName(oldRedB[0].Needles[0].NightFile) == "needle_prs_low_n_E217.png");
            Check("needles（CC0）に E217 と同じ画像は入っていない", !File.Exists(Path.Combine(NeedleCatalog.Folder, "nd_prs2_low.png")) && !File.Exists(Path.Combine(NeedleCatalog.Folder, "nd_prs2_low_dark.png")));
            // 書き直すと ..\needle_E217\… の形になる
            Check("E217 の針は needles.txt に「..\\needle_E217\\…」で書く", NeedleCatalog.Format(presets).Any(l => l.Contains(@"..\needle_E217\needle_speed_E217.png")));

            // 画像から見つけた回転の中心と、表（作者の既存パネルの値）を比べる
            foreach (var n in presets.SelectMany(p => p.Needles).DistinctBy(n => n.DayFile))
            {
                var px = ImageLoader.TryLoadPixels(n.DayFile)!;
                var r = NeedlePivot.Find(px);
                if (r == null) { log.Add($"     {Path.GetFileName(n.DayFile)}: 表 {n.OriginX},{n.OriginY} ／ 画像から見つけられない（薄い影の画像など）"); continue; }
                bool near = Math.Abs(r.X - n.OriginX) <= 1 && Math.Abs(r.Y - n.OriginY) <= 4;
                log.Add($"     {Path.GetFileName(n.DayFile)}: 表 {n.OriginX},{n.OriginY} ／ 画像から {r.X},{r.Y}（{(near ? "ほぼ一致" : "違う")}・{r.Reason}）");
            }

            // 選ぶ画面
            var w = new NeedlePickerWindow(this);
            w.Show();
            await Task.Delay(400);
            w.SelectPresetForTest("時計（");
            await Task.Delay(300);
            SaveWindowPng(w, Path.ChangeExtension(png, ".picker.png"));
            var clock = w.CurrentForTest.Select(n => n.Clone()).ToList();
            w.SelectPresetForTest("圧力計Ａ");
            var prsA = w.CurrentForTest.Select(n => n.Clone()).ToList();
            w.SelectPresetForTest("圧力計Ｂ");
            var prsB = w.CurrentForTest.Select(n => n.Clone()).ToList();
            log.Add("     標準の針の名前: " + string.Join("、", NeedleCatalog.Presets().Select(p => p.Label)));
            w.SelectPresetForTest("速度計の針Ａ");
            var speed = w.CurrentForTest.Select(n => n.Clone()).ToList();
            Check("固有角振動数・減衰比も置く前に見えて直せる（速度計は 6.3・0.5）", w.FieldForTest("NaturalFreq") == "6.3" && w.FieldForTest("DampingRatio") == "0.5");
            Check("回転の中心・角度の説明は出さない", w.PivotNoteForTest.Length == 0);
            w.SetFieldForTest("NaturalFreq", "8");
            w.SetFieldForTest("DampingRatio", "0.7");
            var tuned = w.CurrentForTest[0].Clone();
            Check("直した値が置く針に入る", tuned.NaturalFreq == 8 && tuned.DampingRatio == 0.7);
            w.SelectPresetForTest("時計（");
            Check("組の名前（時計）", w.PresetNameForTest == "時計");
            Check("設定する針は上に重なる針から（時計は 秒針・分針・時針）", w.WhichForTest.SequenceEqual(["時計 秒針", "時計 分針", "時計 時針"]));
            w.SelectPresetForTest("速度計(E217)");
            Check("影の針は最後（速度計(E217) は 針・影 の順。最初は針を設定する）", w.WhichForTest.SequenceEqual(["速度計(E217)", "速度計の影(E217)"]) && w.FieldForTest("OriginX") == "8");
            w.Close();
            Check("時計は 3 本", clock.Count == 3 && clock.Select(n => n.Subject).SequenceEqual(["hour", "min", "sec"]));
            await NeedleListEditTest(Check, log);

            int before = _doc!.Sections.Count;
            var center = _doc.This is { } t && PanelValues.TryPoint(t.Get("Center"), out var cx, out var cy) ? new Point(cx, cy) : Canvas.ViewCenterPanel;
            var a = PlaceNeedles(speed, new Point(center.X - 300, center.Y));
            var b = PlaceNeedles(clock, new Point(center.X + 300, center.Y), "時計");
            Check("速度計 1 本・時計 3 本を置ける", a.Count == 1 && b.Count == 3 && _doc.Sections.Count == before + 4);
            var sp = _doc.Sections[a[0]];
            Check("速度計の値（作者の既存パネルと同じ: Origin 16,225・半径 60・-125〜125・0〜160・NaturalFreq 6.3）",
                sp.Get("Origin") == "16, 225" && sp.Get("Radius") == "60" && sp.Get("InitialAngle") == "-125" && sp.Get("LastAngle") == "125"
                && sp.Get("Maximum") == "160" && sp.Get("Subject") == "kmph" && sp.Get("NaturalFreq") == "6.3");
            Check("画像をパネルの needle フォルダにコピーし、そこを指す", sp.Get("DaytimeImage") == Path.Combine("needle", "nd_speed.png")
                && File.Exists(Path.Combine(PanelDir!, "needle", "nd_speed.png")) && sp.Get("NighttimeImage") == Path.Combine("needle", "nd_speed_dark.png"));
            var layers = b.Select(i => PanelValues.Double(_doc.Sections[i].Get("Layer"), 0)).ToList();
            Check("時計の 3 本はグループ「時計」になる", b.All(i => _doc.Sections[i].Group == "時計"));
            Check("1 本だけの速度計はグループにしない", _doc.Sections[a[0]].Group == null);
            Check("時計の 3 本は同じ位置で、時・分・秒の順に手前", b.Select(i => _doc.Sections[i].Get("Location")).Distinct().Count() == 1 && layers[0] < layers[1] && layers[1] < layers[2]);
            // もう一度置いても、同じ画像は増やさない
            PlaceNeedles(speed, center);
            Check("同じ画像はコピーし直さない（_2 を作らない）", !File.Exists(Path.Combine(PanelDir!, "needle", "nd_speed_2.png")));
            Check("画像の問題はない", !_scene!.Problems.Any(p => p.Message.Contains("画像")));

            // 圧力計Ａ・Ｂ: 赤が下・黒が上で、同じ位置
            Check("圧力計Ａは 赤Ａタイプ（下）＋黒（上）", prsA.Count == 2 && Path.GetFileName(prsA[0].DayFile) == "nd_prs1_low.png" && Path.GetFileName(prsA[1].DayFile) == "nd_prs_up.png");
            Check("圧力計Ｂは 赤Ｂタイプ（下）＋黒（上）", prsB.Count == 2 && Path.GetFileName(prsB[0].DayFile) == "needle_prs_low_E217.png" && Path.GetFileName(prsB[1].DayFile) == "nd_prs_up.png");
            var pa = PlaceNeedles(prsA, new Point(center.X, center.Y + 300), "圧力計Ａ");
            Check("圧力計Ａの 2 本はグループ「圧力計Ａ」になり、グループごと選ぶ", pa.All(i => _doc.Sections[i].Group == "圧力計Ａ") && _selGroup == "圧力計Ａ");
            Check("グループごと選んでいても「蓋を作る」が出る（2 本まとめて）", CoverButton.Visibility == Visibility.Visible && CoverTargets().Count == 2);
            var tunedAt = PlaceNeedles([tuned], new Point(center.X - 300, center.Y + 300));
            // 速度計Ｃ・E217
            var all = NeedleCatalog.Builtin();
            var c = all.First(p => p.Label == "速度計の針Ｃタイプ").Needles[0];
            Check("速度計の針Ｃタイプ（speed_toyo・半径 248・中心 32,252・6.3／0.5）", Path.GetFileName(c.DayFile) == "speed_toyo.png" && File.Exists(c.DayFile) && File.Exists(c.NightFile!)
                && c.Radius == 248 && c.OriginX == 32 && c.OriginY == 252 && c.InitialAngle == -125 && c.NaturalFreq == 6.3 && c.DampingRatio == 0.5);
            var e217 = PlaceNeedles(all.First(p => p.Label == "圧力計(E217)").Needles, new Point(center.X + 300, center.Y + 300), "圧力計(E217)");
            var es = e217.Select(i => _doc.Sections[i]).ToList();
            log.Add("     圧力計(E217): " + string.Join(" ／ ", es.Select(x => $"{x.Title} {x.Get("Location")} L{x.Get("Layer")}")));
            PanelValues.TryPoint(es[2].Get("Location"), out var mx, out var my);
            Check("圧力計(E217) は 4 本（影 2・赤・黒）で、panel_E217.txt と同じ位置関係", es.Count == 4 && es.All(x => x.Group == "圧力計(E217)")
                && es[0].Get("Location") == PanelValues.Point(mx + 7, my + 7) && es[1].Get("Location") == PanelValues.Point(mx + 11, my + 11) && es[3].Get("Location") == PanelValues.Point(mx - 3, my - 3));
            Check("E217 の黒い針は TransparentColor 000000・値は panel_E217.txt と同じ（-130.8〜136.5・10〜1000・80／0.1）", es[3].Get("TransparentColor") == "000000"
                && es[2].Get("InitialAngle") == "-130.8" && es[2].Get("LastAngle") == "136.5" && es[2].Get("Minimum") == "10" && es[2].Get("Maximum") == "1000" && es[2].Get("NaturalFreq") == "80" && es[2].Get("DampingRatio") == "0.1");
            Check("StopPin は書式 2.00 のパネルにも書く", es[2].Get("StopPin") == "True");
            Check("E217 の画像は exe の needle_E217 フォルダにあり、パネルにコピーする", es.All(x => File.Exists(Path.Combine(PanelDir!, x.Get("DaytimeImage")!))) && es[3].Get("DaytimeImage")!.EndsWith("needle_prs_up_E217.png"));
            var sp217 = PlaceNeedles(all.First(p => p.Label == "速度計(E217)").Needles, new Point(center.X - 300, center.Y - 300), "速度計(E217)");
            Check("速度計(E217) は影＋針（影は 6, 3 ずらして下）", sp217.Count == 2 && _doc.Sections[sp217[0]].Title == "速度計の影(E217)"
                && PanelValues.Double(_doc.Sections[sp217[0]].Get("Layer"), 0) < PanelValues.Double(_doc.Sections[sp217[1]].Get("Layer"), 0)
                && _doc.Sections[sp217[1]].Get("Radius") == "180" && _doc.Sections[sp217[1]].Get("NaturalFreq") == "6.3");
            ApplySelection(pa[0], pa, "圧力計Ａ", SelectionSource.Other);
            Canvas.AllowFreshEdit(pa);
            Check("直した固有角振動数・減衰比がパネルに入る", _doc.Sections[tunedAt[0]].Get("NaturalFreq") == "8" && _doc.Sections[tunedAt[0]].Get("DampingRatio") == "0.7");
            _doc.Sections.RemoveAt(tunedAt[0]);
            CommitModel();
            RebuildAll();
            ApplySelection(pa[0], pa, "圧力計Ａ", SelectionSource.Other);
            Canvas.AllowFreshEdit(pa);
            var red = _doc.Sections[pa[0]]; var black = _doc.Sections[pa[1]];
            Check("圧力計は 固有角振動数 80・減衰比 0.1", red.Get("NaturalFreq") == "80" && red.Get("DampingRatio") == "0.1" && black.Get("NaturalFreq") == "80");
            Check("圧力計Ａを置くと 2 本が同じ位置・赤が下", red.Get("Location") == black.Get("Location")
                && PanelValues.Double(red.Get("Layer"), 0) < PanelValues.Double(black.Get("Layer"), 0) && red.Get("Subject") == "mr" && black.Get("Subject") == "bc");
            Check("置いても位置ロックはそのまま・置いた針だけ動かせる", Canvas.PositionLocked && !Canvas.EditLocked);

            // 大きさ: 2 本まとめて選んだまま ＋ キーと同じ操作で半径を大きくすると、2 本とも同じ比で大きくなる
            Check("＋ キーで 2 本まとめて半径が 1px 大きくなる", Canvas.StepRadiusForTest(1) && red.Get("Radius") == "62" && black.Get("Radius") == "62");
            for (int k = 0; k < 4; k++) Canvas.StepRadiusForTest(5);
            Check("Shift＋＋ で 5px ずつ（82）", red.Get("Radius") == "82" && black.Get("Radius") == "82");
            Check("テキストにも入る", Editor.Text.Contains("Radius = 82"));
            // ほかを選ぶとロックに戻り、元の針を選び直しても動かせない
            SelectForStartup(_doc.Sections.IndexOf(_doc.This!));
            ApplySelection(pa[0], pa, null, SelectionSource.Other);
            Check("ほかを選ぶとロックに戻る（選び直しても大きさを変えない）", Canvas.EditLocked && !Canvas.StepRadiusForTest(1) && red.Get("Radius") == "82");
            SetLocked(false);
            Canvas.ZoomCenter(1.5);
            Canvas.CenterOn(new Point(center.X, center.Y + 300));
            UpdateLayout();
            await Task.Delay(400);
            SaveWindowPng(this, Path.ChangeExtension(png, ".radius.png"));

            // 動作プレビューで 80 km/h・10:10:30 にして見る
            Canvas.State["kmph"] = 80; Canvas.State["hour"] = 10; Canvas.State["min"] = 10; Canvas.State["sec"] = 30;
            Canvas.InvalidateVisual();
            Canvas.ZoomCenter(1.2);
            Canvas.CenterOn(center);
            UpdateLayout();
            await Task.Delay(800);
            SaveWindowPng(this, png);
        }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); fail++; }
        log.Add(fail == 0 ? "ALL PASSED" : $"{fail} FAILED");
        File.WriteAllLines(report, log);
        _savedText = Editor.Text;
        Close();
    }
}
