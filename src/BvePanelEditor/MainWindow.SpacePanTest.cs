using System.IO;
using System.Windows;
using System.Windows.Input;
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

            // スペースなしの左ドラッグは、今までどおり計器を動かす
            Canvas.DragForTest(lamp, to, MouseButton.Left);
            Check("スペースなしの左ドラッグは計器を動かす", Loc() != "100, 100");
            LoadPanel(panel, null);
            await Task.Delay(200);
            Canvas.PositionLocked = false;
            Canvas.FitAll();
            UpdateLayout();
            lamp = Canvas.ToScreen(new Point(132, 132));
            to = lamp + new Vector(40, 30);

            // スペースを押しながら左ドラッグは、表示だけを動かす（計器は動かさない・テキストは変わらない）
            Canvas.Space.SetHeldForTest(true);
            Check("スペースを押している間はマウスの形が変わる", Canvas.Cursor == Rendering.SpacePan.Cursor);
            var o0 = Canvas.OffsetForTest;
            Canvas.DragForTest(lamp, to, MouseButton.Left);
            var moved = Canvas.OffsetForTest - o0;
            log.Add($"     スペース＋左ドラッグで表示が動いた量: {moved.X:0.#}, {moved.Y:0.#}");
            Check("スペース＋左ドラッグで表示が動く（ドラッグした分だけ）", Math.Abs(moved.X - 40) < 0.5 && Math.Abs(moved.Y - 30) < 0.5);
            Check("計器は動かない・テキストは変わらない", Loc() == "100, 100" && Editor.Text == File.ReadAllText(panel));
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
        }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); fail++; }
        log.Add(fail == 0 ? "ALL PASSED" : $"{fail} FAILED");
        File.WriteAllLines(report, log);
        _savedText = Editor.Text;
        Close();
    }
}
