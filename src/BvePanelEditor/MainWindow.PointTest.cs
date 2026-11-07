using System.IO;
using System.Windows;
using BvePanelEditor.Core;
using BvePanelEditor.Rendering;

namespace BvePanelEditor;

/// <summary>確認用: 中心・消失点を選んで矢印キーと同じく動かす（--pointtest 画面.png 結果.txt。パネルファイルは保存しない）。</summary>
public partial class MainWindow
{
    public async void RunPointTest(string png, string report)
    {
        var log = new List<string>();
        int fail = 0;
        void Check(string name, bool ok) { log.Add($"{(ok ? "OK  " : "FAIL")} {name}"); if (!ok) fail++; }
        try
        {
            var self = _doc!.This!;
            int idx = _doc.Sections.IndexOf(self);
            SelectForStartup(idx);
            var c0 = self.Get("Center")!;
            var o0 = self.Get("Origin")!;
            log.Add($"     はじめ: Center={c0} Origin={o0}");

            Canvas.ActivePoint = PanelCanvas.PanelPoint.Center;
            SetLocked(false);
            Canvas.MoveActivePoint(new Vector(1, 0));
            Canvas.MoveActivePoint(new Vector(1, 0));
            Canvas.MoveActivePoint(new Vector(0, 10));
            PanelValues.TryPoint(c0, out var cx, out var cy);
            Check("中心が矢印キーと同じく動く（右 2px・下 10px）", self.Get("Center") == PanelValues.Point(cx + 2, cy + 10));
            Check("消失点は動かない", self.Get("Origin") == o0);
            Check("テキストにも入る", Editor.Text.Contains($"Center = {PanelValues.Point(cx + 2, cy + 10)}"));

            Canvas.ActivePoint = PanelCanvas.PanelPoint.Origin;
            Canvas.MoveActivePoint(new Vector(-10, 0));
            PanelValues.TryPoint(o0, out var ox, out var oy);
            Check("消失点も動かせる", self.Get("Origin") == PanelValues.Point(ox - 10, oy));

            // ほかの計器を選ぶと、中心・消失点の選択は外れる
            if (_doc.Sections.Count > idx + 1)
            {
                SelectForStartup(idx + 1);
                Check("ほかの計器を選ぶと外れる", Canvas.ActivePoint == PanelCanvas.PanelPoint.None);
                SelectForStartup(idx);
            }
            // 両方選ぶ: 矢印キーで一緒に動き、横の位置をそろえられる
            Canvas.ActivePoint = PanelCanvas.PanelPoint.Both;
            var cBefore = self.Get("Center")!; var oBefore = self.Get("Origin")!;
            PanelValues.TryPoint(cBefore, out var c1x, out var c1y);
            PanelValues.TryPoint(oBefore, out var o1x, out var o1y);
            Canvas.MoveActivePoint(new Vector(0, -5));
            Check("両方選ぶと矢印キーで一緒に動く", self.Get("Center") == PanelValues.Point(c1x, c1y - 5) && self.Get("Origin") == PanelValues.Point(o1x, o1y - 5));
            Check("横の位置をそろえる前は X が違う", c1x != o1x);
            Check("消失点を中心に合わせられる", Canvas.AlignPointsX(originToCenter: true)
                && self.Get("Origin") == PanelValues.Point(c1x, o1y - 5) && self.Get("Center") == PanelValues.Point(c1x, c1y - 5));
            Canvas.MoveActivePoint(new Vector(7, 0));
            Check("中心を消失点に合わせられる", Canvas.AlignPointsX(originToCenter: false) && self.Get("Center") == PanelValues.Point(c1x + 7, c1y - 5));
            // 消失点を少し下へずらして、そろった印（縦の線）が見えるようにする
            Canvas.ActivePoint = PanelCanvas.PanelPoint.Origin;
            Canvas.MoveActivePoint(new Vector(0, 150));
            Canvas.ActivePoint = PanelCanvas.PanelPoint.Both;
            SetLocked(true);
            Check("位置ロック中はそろえない", !Canvas.AlignPointsX(originToCenter: true));
            Canvas.CenterOn(new Point(cx, cy));
            await Task.Delay(400);
            SaveWindowPng(this, png);
        }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); fail++; }
        log.Add(fail == 0 ? "ALL PASSED" : $"{fail} FAILED");
        File.WriteAllLines(report, log);
        _savedText = Editor.Text;
        Close();
    }
}
