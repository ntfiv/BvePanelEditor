using System.IO;
using System.Windows;
using BvePanelEditor.Core;
using BvePanelEditor.Views;

namespace BvePanelEditor;

/// <summary>
/// 確認用: 画像のドロップで部品として置く（--droptest 表示灯の画像 数字の画像 針の画像 画面.png 結果.txt）。
/// 開いたパネルのコピーに対して使う（保存はしない。パネルのフォルダの外の画像はコピーする）。
/// </summary>
public partial class MainWindow
{
    public async void RunDropTest(string lampImage, string digitImage, string needleImage, string png, string report)
    {
        var log = new List<string>();
        int fail = 0;
        void Check(string name, bool ok) { log.Add($"{(ok ? "OK  " : "FAIL")} {name}"); if (!ok) fail++; }
        try
        {
            string? asked = null;
            ChoiceDialog.TestAnswer = (msg, items) => { asked = msg + " ／ " + string.Join(" | ", items); return 0; };
            var at = new Point(1000, 800);
            int before = _doc!.Sections.Count;
            HandleDrop([lampImage], at);
            log.Add("     聞かれたこと: " + asked);
            Check("パネルを開いていると、どう読み込むか聞く", asked != null && asked.Contains("常点灯画像として読み込む"));
            var lamp = _doc.Sections[^1];
            var px = Rendering.ImageLoader.TryLoadPixels(lampImage)!;
            Check("表示灯として置く（常表示）", _doc.Sections.Count == before + 1 && lamp.Kind == SectionKind.PilotLamp && lamp.Get("Subject") == "true");
            Check("ドロップした所を中心に置く", lamp.Get("Location") == PanelValues.Point(Math.Round(1000 - px.Width / 2.0), Math.Round(800 - px.Height / 2.0)));
            var rel = lamp.Get("DaytimeImage")!;
            Check("パネルのフォルダの外の画像は、パネルのフォルダにコピーして指す", !rel.StartsWith("..") && File.Exists(Path.Combine(PanelDir!, rel)));
            log.Add($"     表示灯: {lamp.Get("Location")}　昼 {rel}　夜 {lamp.Get("NighttimeImage") ?? "なし"}");
            // 位置ロックはそのままで、置いた表示灯だけ動かせる
            void PressRight() => Canvas.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(Canvas)!, 0, System.Windows.Input.Key.Right) { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
            int li = _doc.Sections.IndexOf(lamp);
            var loc0 = lamp.Get("Location");
            PanelValues.TryPoint(loc0, out var lx, out var ly);
            PressRight();
            Check("位置ロック中でも、置いた表示灯は矢印キーで動く", Canvas.PositionLocked && lamp.Get("Location") == PanelValues.Point(lx + 1, ly));
            int other = _doc.Sections.IndexOf(_doc.This!);
            SelectForStartup(other);
            SelectForStartup(li);
            var loc1 = lamp.Get("Location");
            PressRight();
            Check("ほかを選んだあとは、ロックに戻って動かない", lamp.Get("Location") == loc1 && Canvas.EditLocked);
            Check("夜の画像も名前から探す", NeedlePickerWindow.FindNight(lampImage) == null || lamp.Get("NighttimeImage") != null);

            // 2 枚まとめて
            ChoiceDialog.TestAnswer = (_, _) => 0;
            int b2 = _doc.Sections.Count;
            HandleDrop([lampImage, lampImage], at);
            Check("2 枚まとめて置くと少しずらす", _doc.Sections.Count == b2 + 2 && _doc.Sections[^1].Get("Location") != _doc.Sections[^2].Get("Location"));
            Check("同じ画像はコピーし直さない", !File.Exists(Path.Combine(PanelDir!, Path.GetFileNameWithoutExtension(lampImage) + "_2.png")));

            // デジタル数字（コマ割済。大きさが 2 の累乗でなければ「このまま使う」、テスト画面はそのまま「置く」）
            ChoiceDialog.TestAnswer = (msg, _) => msg.Contains("どう読み込みますか") ? 1 : 1;
            DigitTestWindow.TestAnswer = _ => true;
            HandleDrop([digitImage], at);
            DigitTestWindow.TestAnswer = null;
            var dn = _doc.Sections[^1];
            Check("デジタル数字として置き、1 コマの高さを入れる", dn.Kind == SectionKind.DigitalNumber && PanelValues.Double(dn.Get("Interval"), 0) > 0);
            log.Add($"     デジタル数字: Interval {dn.Get("Interval")}　{MessageText.Text}");

            // 針: 画面をその画像で開く
            var w = new NeedlePickerWindow(this, needleImage);
            w.Show();
            await Task.Delay(400);
            var n = w.CurrentForTest.FirstOrDefault();
            Check("指針の画面が、ドロップした画像で開く", n != null && string.Equals(Path.GetFullPath(n.DayFile), Path.GetFullPath(needleImage), StringComparison.OrdinalIgnoreCase));
            log.Add($"     針: 回転の中心 {n?.OriginX},{n?.OriginY}");
            SaveWindowPng(w, Path.ChangeExtension(png, ".needle.png"));
            w.Close();

            ChoiceDialog.TestAnswer = null;
            Canvas.CenterOn(at);
            Canvas.ZoomCenter(0.6);
            UpdateLayout();
            await Task.Delay(400);
            SaveWindowPng(this, png);
        }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); fail++; }
        ChoiceDialog.TestAnswer = null;
        log.Add(fail == 0 ? "ALL PASSED" : $"{fail} FAILED");
        File.WriteAllLines(report, log);
        _savedText = Editor.Text;
        Close();
    }
}
