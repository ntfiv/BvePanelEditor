using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BvePanelEditor.Core;

namespace BvePanelEditor;

/// <summary>
/// 確認用: スペースキーを押しながら左ドラッグで表示を動かす（--spacepantest 作業フォルダ 結果.txt）。
/// スペースを押したことは確認用の口で入れる（本物のキー・マウスは使わない）。
/// </summary>
public partial class MainWindow
{
    public async void RunSpacePanTest(string workDir, string report)
    {
        var log = new List<string>();
        int fail = 0;
        void Check(string name, bool ok) { log.Add($"{(ok ? "OK  " : "FAIL")} {name}"); if (!ok) fail++; }
        try
        {
            Directory.CreateDirectory(workDir);
            var px = new byte[64 * 64 * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i + 2] = 220; px[i + 3] = 255; }
            File.WriteAllBytes(Path.Combine(workDir, "lamp.png"), Core.Psd.PngWriter.Encode(64, 64, px));
            var text = "BveTs Instrument Panel 2.00\r\n\r\n[This]\r\nResolution = 400\r\n\r\n;灯\r\n[PilotLamp]\r\nSubject = true\r\nLocation = 100, 100\r\nDaytimeImage = lamp.png\r\n";
            var panel = Path.Combine(workDir, "panel.txt");
            File.WriteAllText(panel, text);
            LoadPanel(panel, null);
            await Task.Delay(300);
            Canvas.PositionLocked = false;
            Canvas.FitAll();
            UpdateLayout();
            var lamp = Canvas.ToScreen(new Point(132, 132));
            var to = lamp + new Vector(40, 30);
            string Loc() => _doc!.Sections.First(s => s.Kind == SectionKind.PilotLamp).Get("Location") ?? "";

            // スペースなしの左ドラッグは、今までどおりオブジェクトを動かす
            Canvas.DragForTest(lamp, to, MouseButton.Left);
            Check("スペースなしの左ドラッグはオブジェクトを動かす", Loc() != "100, 100");
            LoadPanel(panel, null);
            await Task.Delay(200);
            Canvas.PositionLocked = false;
            Canvas.FitAll();
            UpdateLayout();
            lamp = Canvas.ToScreen(new Point(132, 132));
            to = lamp + new Vector(40, 30);

            // スペースを押しながら左ドラッグは、表示だけを動かす（オブジェクトは動かさない・テキストは変わらない）
            Canvas.Space.SetHeldForTest(true);
            Check("スペースを押している間はマウスの形が変わる", Canvas.Cursor == Rendering.SpacePan.Cursor);
            var o0 = Canvas.OffsetForTest;
            Canvas.DragForTest(lamp, to, MouseButton.Left);
            var moved = Canvas.OffsetForTest - o0;
            log.Add($"     スペース＋左ドラッグで表示が動いた量: {moved.X:0.#}, {moved.Y:0.#}");
            Check("スペース＋左ドラッグで表示が動く（ドラッグした分だけ）", Math.Abs(moved.X - 40) < 0.5 && Math.Abs(moved.Y - 30) < 0.5);
            Check("オブジェクトは動かない・テキストは変わらない", Loc() == "100, 100" && Editor.Text == File.ReadAllText(panel));
            Canvas.Space.SetHeldForTest(false);
            Check("スペースを離すとマウスの形が戻る", Canvas.Cursor == null);

            // 中ボタン・右ボタンを押している間も、移動の形にする（離すと戻る）
            foreach (var b in new[] { MouseButton.Middle, MouseButton.Right })
            {
                Canvas.PressForTest(lamp, b);
                bool during = Canvas.Cursor == Rendering.SpacePan.Cursor;
                Canvas.EndDragForTest();
                Check($"{(b == MouseButton.Middle ? "中" : "右")}ボタンを押している間はマウスの形が変わり、離すと戻る", during && Canvas.Cursor != Rendering.SpacePan.Cursor);
            }
            // 中ボタン・右ボタンのドラッグも、今までどおり表示を動かす
            foreach (var b in new[] { MouseButton.Middle, MouseButton.Right })
            {
                var o1 = Canvas.OffsetForTest;
                Canvas.DragForTest(lamp, to, b);
                var m = Canvas.OffsetForTest - o1;
                Check($"{(b == MouseButton.Middle ? "中" : "右")}ボタンのドラッグでも表示が動く", Math.Abs(m.X - 40) < 0.5 && Math.Abs(m.Y - 30) < 0.5 && Loc() == "100, 100");
            }
            // 位置ロック中でも、スペース＋左ドラッグで表示は動く
            Canvas.PositionLocked = true;
            Canvas.Space.SetHeldForTest(true);
            var o2 = Canvas.OffsetForTest;
            Canvas.DragForTest(Canvas.ToScreen(new Point(132, 132)), Canvas.ToScreen(new Point(132, 132)) + new Vector(-25, 10), MouseButton.Left);
            var m2 = Canvas.OffsetForTest - o2;
            Check("位置ロック中でも、スペース＋左ドラッグで表示は動く", Math.Abs(m2.X + 25) < 0.5 && Math.Abs(m2.Y - 10) < 0.5);
            Canvas.Space.SetHeldForTest(false);

            // 全体を表示: 表示範囲（Left・Top・Right・Bottom）があれば、それが画面の真ん中に収まる
            Rect Shown(Rect r) => new(Canvas.ToScreen(r.TopLeft), Canvas.ToScreen(r.BottomRight));
            var cw = Canvas.ActualWidth; var chh = Canvas.ActualHeight;
            bool Fits(Rect s) => s.Left >= -0.5 && s.Top >= -0.5 && s.Right <= cw + 0.5 && s.Bottom <= chh + 0.5
                && Math.Abs((s.Left + s.Right) / 2 - cw / 2) < 1 && Math.Abs((s.Top + s.Bottom) / 2 - chh / 2) < 1
                && (Math.Abs(s.Width - Math.Min(cw - 56, cw * 0.95)) < 1 || Math.Abs(s.Height - Math.Min(chh - 56, chh * 0.95)) < 1);
            Canvas.FitAll();
            Check("表示範囲がなければ、画像のある範囲（0, 0 から）を収める", Fits(Shown(Canvas.FitArea)) && Canvas.FitArea.TopLeft == new Point(0, 0));
            File.WriteAllText(panel, text.Replace("Resolution = 400\r\n", "Resolution = 400\r\nLeft = 120\r\nTop = 110\r\nRight = 170\r\nBottom = 150\r\n"));

            LoadPanel(panel, null);
            await Task.Delay(300);
            Canvas.FitAll();
            var lim = new Rect(120, 110, 50, 40);
            log.Add($"     全体を表示で収める範囲: {Canvas.FitArea}　画面では {Shown(lim)}（画面 {cw:0}×{chh:0}）");
            Check("全体を表示は、表示範囲（Left・Top・Right・Bottom）を画面の真ん中に収める", Canvas.FitArea == lim && Fits(Shown(lim)));
            Check("画面の右下に操作の案内を出す（マウスは素通り）", CanvasHelp.IsVisible && !CanvasHelp.IsHitTestVisible);
            var help = CanvasHelp.TransformToVisual(Canvas).TransformBounds(new Rect(CanvasHelp.RenderSize));
            Check("全体を表示したとき、操作の案内は表示範囲に重ならない", !help.IntersectsWith(Shown(lim)));
            SaveWindowPng(this, Path.ChangeExtension(report, ".png"));

            // 重なったオブジェクト: 右クリックの一覧・選んでいるオブジェクトを上から動かす
            var text2 = "BveTs Instrument Panel 2.00\r\n\r\n[This]\r\nResolution = 400\r\n\r\n;下\r\n[PilotLamp]\r\nSubject = true\r\nLocation = 100, 100\r\nDaytimeImage = lamp.png\r\nLayer = 1\r\n"
                + "\r\n;上\r\n[PilotLamp]\r\nSubject = true\r\nLocation = 120, 120\r\nDaytimeImage = lamp.png\r\nLayer = 2\r\n";
            File.WriteAllText(panel, text2);
            LoadPanel(panel, null);
            await Task.Delay(300);
            Canvas.PositionLocked = false;
            Canvas.FitAll();
            UpdateLayout();
            var both = new Point(140, 140);
            var labels = Canvas.ItemsMenuEntries(both);
            log.Add("     右クリックの一覧: " + string.Join(" ／ ", labels.Select(l => l.Label)));
            Check("右クリックの一覧に、重なったオブジェクトが上から順に出る", labels.Count == 2 && labels[0].Label.StartsWith("上") && labels[1].Label.StartsWith("下"));
            Canvas.SelectItemDirect(labels[1].Section);
            Check("一覧から下のオブジェクトを選べる", _selected == labels[1].Section);
            Canvas.DragForTest(Canvas.ToScreen(both), Canvas.ToScreen(both) + new Vector(30, 0), MouseButton.Left);
            string L(string t) => _doc!.Sections.First(s => s.Title == t).Get("Location") ?? "";
            log.Add($"     上からドラッグしたあと: 下 {L("下")}・上 {L("上")}");
            Check("選んでいる下のオブジェクトの上でドラッグすると、上に重なっていても下のオブジェクトを動かす", L("下") != "100, 100" && L("上") == "120, 120" && _selected == labels[1].Section);
            Canvas.DragForTest(Canvas.ToScreen(both), Canvas.ToScreen(both), MouseButton.Left);
            Check("動かさずにクリックすると、いつもどおりいちばん上のオブジェクトを選ぶ", _selected == labels[0].Section);

            // オブジェクトを追加すると、選んでいるオブジェクトのすぐ上（ファイルで 1 つ前）に、同じレイヤーで入る
            int lower = _doc!.Sections.FindIndex(s => s.Title == "下");
            ApplySelection(lower, [lower], null, SelectionSource.Other);
            { var ns = PanelSection.Create(SectionKind.PilotLamp); ns.Set("Layer", "9"); _selected = InsertNewSection(ns, GetInsertPlace(), 0); CommitModel(); SelectSection(_selected); } // 追加（メニュー・ドロップと同じ入れ方）
            Check("追加したオブジェクトは、選んでいたオブジェクトのすぐ下（ファイルで 1 つ後ろ＝BVE ではすぐ手前）に入る", _selected == lower + 1 && _doc.Sections[lower].Title == "下");
            Check("追加したオブジェクトのレイヤーは、選んでいたオブジェクトと同じ", _doc.Sections[lower + 1].Get("Layer") == "1");
            SelectSection(-1);
            { var ns = PanelSection.Create(SectionKind.PilotLamp); ns.Set("Layer", "9"); _selected = InsertNewSection(ns, GetInsertPlace(), 0); CommitModel(); SelectSection(_selected); } // 追加（メニュー・ドロップと同じ入れ方）
            Check("何も選んでいなければ、今までどおりファイルの最後に入る", _selected == _doc.Sections.Count - 1);

            // レイヤーをそろえる: 「ふた」（L3・ファイルで前）と「針」（L1・ファイルで後ろ）が重なっていて、L2 の「遠く」にそろえると手前・奥が入れ替わる
            var text3 = "BveTs Instrument Panel 2.00\r\n\r\n[This]\r\nResolution = 400\r\n\r\n;ふた\r\n[PilotLamp]\r\nSubject = true\r\nLocation = 100, 100\r\nDaytimeImage = lamp.png\r\nLayer = 3\r\n"
                + "\r\n;針\r\n[PilotLamp]\r\nSubject = true\r\nLocation = 110, 110\r\nDaytimeImage = lamp.png\r\nLayer = 1\r\n"
                + "\r\n;遠く\r\n[PilotLamp]\r\nSubject = true\r\nLocation = 300, 300\r\nDaytimeImage = lamp.png\r\nLayer = 2\r\n";
            File.WriteAllText(panel, text3);
            LoadPanel(panel, null);
            await Task.Delay(300);
            ApplySelection(3, [3, 1, 2], null, SelectionSource.Other); // 主に選んでいるのは「遠く」（L2）
            string asked = "";
            _autoConfirm = m => { asked = m; return false; };
            ChangeLayers(Views.LayerOp.Unify);
            log.Add("     そろえるときの確認: " + asked.Replace("\n", " ").Trim());
            Check("重なっているオブジェクトの手前・奥が入れ替わるときは確認し、やめたら変えない", asked.Contains("ふた") && asked.Contains("針") && Editor.Text == File.ReadAllText(panel));
            _autoConfirm = _ => true;
            ChangeLayers(Views.LayerOp.Unify);
            Check("続けると、主に選んでいるオブジェクトのレイヤー（2）にそろう", _doc!.Sections.Skip(1).All(s => s.Get("Layer") == "2"));
            _autoConfirm = m => { asked = m; return false; };
            asked = "";
            ApplySelection(2, [2, 3], null, SelectionSource.Other);
            OnUndo(this, null!);
            ApplySelection(1, [1, 3], null, SelectionSource.Other); // ふた（L3）と遠く（L2）は重ならない
            ChangeLayers(Views.LayerOp.Unify);
            Check("重ならないオブジェクトどうしなら確認せずにそろえる", asked == "" && _doc!.Sections[3].Get("Layer") == "3");
            // 任意のレイヤーにそろえる
            _autoConfirm = _ => true;
            ApplySelection(1, [1, 2, 3], null, SelectionSource.Other);
            UnifyLayers(LayerTargets(), 5);
            Check("任意のレイヤー（5）にもそろえられる", _doc!.Sections.Skip(1).All(s => s.Get("Layer") == "5"));
            // 一覧の下の「そろえる ▾」
            _doc!.Sections[1].Set("Layer", "3"); _doc.Sections[2].Set("Layer", "1"); _doc.Sections[3].Set("Layer", "2"); CommitModel();
            ApplySelection(3, [3, 1, 2], null, SelectionSource.Other);
            var um = BuildUnifyLayerMenu();
            var heads = um?.Items.OfType<System.Windows.Controls.MenuItem>().Select(m => m.Header?.ToString() ?? "").ToList() ?? [];
            log.Add("     一覧の下の「そろえる ▾」: " + string.Join(" ／ ", heads));
            Check("一覧の下の「そろえる ▾」は、最初に選んだオブジェクトのレイヤー・使っているレイヤー・ほかのレイヤーを出す",
                heads.Count == 4 && heads[0].StartsWith("2（最初") && heads[1].StartsWith("3（") && heads[2].StartsWith("1（") && heads[3].StartsWith("ほか"));
            um!.Items.OfType<System.Windows.Controls.MenuItem>().ElementAt(1).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            Check("メニューで選んだレイヤー（3）にそろう", _doc!.Sections.Skip(1).All(s => s.Get("Layer") == "3"));
            ApplySelection(1, [1], null, SelectionSource.Other);
            Check("1 つしか選んでいなければメニューは出さない", BuildUnifyLayerMenu() == null);
            Check("一覧の下にレイヤー・グループのボタンがある", UnifyLayerButton.IsVisible);
            _autoConfirm = null;
            ApplySelection(3, [3, 1, 2], null, SelectionSource.Other);
            UpdateLayout();
            SaveWindowPng(this, Path.ChangeExtension(report, ".list.png"));

            // 選んだオブジェクトの枠: 動かせるときは黄色の実線、位置ロックで動かせないときは黄色の点線
            Canvas.PositionLocked = true;
            Check("位置ロック中は、選んだオブジェクトの枠を黄色の点線にする", Canvas.SelectionPen.DashStyle.Dashes.Count > 0 && ((SolidColorBrush)Canvas.SelectionPen.Brush).Color == Color.FromRgb(0xFF, 0xD8, 0x40));
            UpdateLayout();
            SaveWindowPng(this, Path.ChangeExtension(report, ".locked.png"));
            Canvas.PositionLocked = false;
            Check("動かせるときは、選んだオブジェクトの枠を黄色の実線にする", Canvas.SelectionPen.DashStyle.Dashes.Count == 0 && ((SolidColorBrush)Canvas.SelectionPen.Brush).Color == Color.FromRgb(0xFF, 0xD8, 0x40));
            UpdateLayout();
            SaveWindowPng(this, Path.ChangeExtension(report, ".unlocked.png"));

            // ヘルプ → ショートカットキー
            var sw = ShortcutWindowForTest();
            sw.ShowActivated = false;
            sw.Show();
            await Task.Delay(300);
            Check("ショートカットキーの一覧に、説明書の主な操作キーが入っている",
                ShortcutLines.Any(l => l.StartsWith("Ctrl+G")) && ShortcutLines.Any(l => l.StartsWith("Ctrl+0")) && ShortcutLines.Any(l => l.StartsWith("Ctrl+A")));
            SaveWindowPng(sw, Path.ChangeExtension(report, ".keys.png"));
            sw.Close();

            // デジタル数字のテスト画面: 見え方の拡大
            var dpx = new byte[20 * 100 * 4];
            for (int y = 0; y < 100; y++)
                for (int x = 0; x < 20; x++)
                {
                    int o = (y * 20 + x) * 4;
                    bool on = (x + y) % 3 == 0;
                    dpx[o] = (byte)(on ? 40 : 230); dpx[o + 1] = (byte)(on ? 200 : 230); dpx[o + 2] = (byte)(on ? 40 : 230); dpx[o + 3] = 255;
                }
            var dtw = new Views.DigitTestWindow(this, "テスト", new Core.Psd.PsdPixels(new Core.Psd.PsdRect(0, 0, 20, 100), dpx), null, 100, 10, "テスト", "speed");
            dtw.ShowActivated = false;
            dtw.Show();
            await Task.Delay(300);
            Check("見え方は初めは枠に合わせる", dtw.PreviewZoom == null);
            dtw.PreviewZoom = 32;
            dtw.UpdateLayout();
            await Task.Delay(200);
            Check("見え方を 3200% に拡大できる", dtw.PreviewZoom == 32);
            SaveWindowPng(dtw, Path.ChangeExtension(report, ".digit.png"));
            dtw.Close();

        }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); fail++; }
        log.Add(fail == 0 ? "ALL PASSED" : $"{fail} FAILED");
        File.WriteAllLines(report, log);
        _savedText = Editor.Text;
        Close();
    }
}
