using System.IO;
using System.Windows;
using System.Windows.Controls;
using BvePanelEditor.Core;

namespace BvePanelEditor;

/// <summary>
/// 確認用: RealAnalogGauge（--ragtest 作業フォルダ 結果.txt）。
/// 作業フォルダに車両・PluginOrganizer・RealAnalogGauge の設定・パネルを作り、車両から開いて、
/// 自動で探す・プレビューの計算・表の編集・保存・標準の針との相互変換を確かめる。画面は 結果.png・結果.edit.png に保存する。
/// </summary>
public partial class MainWindow
{
    private static IEnumerable<T> FindChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var x in FindChildren<T>(c)) yield return x;
        }
    }

    public async void RunRagTest(string workDir, string report)
    {
        var log = new List<string>();
        int fail = 0;
        void Check(string name, bool ok) { log.Add($"{(ok ? "OK  " : "FAIL")} {name}"); if (!ok) fail++; }
        try
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
            var plugin = Path.Combine(workDir, "Ats", "Plugin", "BveWorkShop");
            var org = Path.Combine(workDir, "Ats", "Organizer");
            var panelDir = Path.Combine(workDir, "Panel");
            Directory.CreateDirectory(plugin);
            Directory.CreateDirectory(org);
            Directory.CreateDirectory(panelDir);
            File.WriteAllText(Path.Combine(plugin, "RealAnalogGauge.dll"), "");
            var parent = Path.Combine(plugin, "RealAnalogGauge.ini");
            File.WriteAllText(parent, "[FileName]\r\nGauge0 = SpeedoMeter\r\n\r\n[DeveloperMode]\r\nEnabled = 0\r\n");
            File.WriteAllText(Path.Combine(plugin, "SpeedoMeter.ini"), "[SubjectKey]\r\nSubject = kmph\r\n\r\n[PanelIndex]\r\nNeedle = 30\r\n\r\n[Vibration]\r\nPeriod = 0.5\r\nAmplitude = 10\r\nThreshold = 52\r\n");
            File.WriteAllText(Path.Combine(plugin, "SpeedoMeter.csv"), "0,-121.5\r\n5,-115\r\n10,-110.5\r\n20,-95.5\r\n140,120\r\n");
            File.WriteAllText(Path.Combine(org, "PluginOrganizer.dll"), "");
            File.WriteAllText(Path.Combine(org, "Instructions.yml"), "version: 1.0.00\r\npip:\r\n  - prio: 0\r\n    exec:\r\n      - run ..\\Plugin\\BveWorkShop\\RealAnalogGauge.dll\r\n");
            // 針の画像（細長い白）
            var px = new byte[8 * 80 * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = 240; px[i + 1] = 240; px[i + 2] = 240; px[i + 3] = 255; }
            File.WriteAllBytes(Path.Combine(panelDir, "needle.png"), Core.Psd.PngWriter.Encode(8, 80, px));
            var panel = Path.Combine(panelDir, "panel.txt");
            File.WriteAllText(panel, "BveTs Instrument Panel 2.00\r\n\r\n[This]\r\nResolution = 400\r\nLeft = 0\r\nTop = 0\r\nRight = 600\r\nBottom = 300\r\n\r\n"
                + ";速度計\r\n[Needle]\r\nSubject = ats30\r\nLocation = 150, 150\r\nDaytimeImage = needle.png\r\nOrigin = 4, 76\r\nInitialAngle = -180\r\nLastAngle = 180\r\nMinimum = 0\r\nMaximum = 360000\r\n\r\n"
                + ";圧力計\r\n[Needle]\r\nSubject = bc\r\nLocation = 400, 150\r\nDaytimeImage = needle.png\r\nOrigin = 4, 76\r\nInitialAngle = -120\r\nLastAngle = 120\r\nMinimum = 0\r\nMaximum = 600\r\n");
            var vehicle = Path.Combine(workDir, "Vehicle.txt");
            File.WriteAllText(vehicle, "BveTs Vehicle 2.00\r\nPanel = Panel\\panel.txt\r\nAts = Ats\\Organizer\\PluginOrganizer.dll\r\n");

            OpenPath(vehicle);
            await Task.Delay(300);
            Check("車両から開くと、PluginOrganizer の指示から RealAnalogGauge の設定を見つける", Rag != null && Rag.IniPath.Equals(Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase));
            Check("針ごとの設定を読む（Gauge0: SpeedoMeter → ats30）", Rag?.ForAts(30) is { Subject: "kmph", Points.Count: 5 });
            Check("問題の欄に RealAnalogGauge の知らせが出る", ProblemList.Items.OfType<ProblemRow>().Any(r => r.InfoMark == "RAG"));
            Check("ファイルを書き換えていない（パネルに場所の行を足さない）", !Editor.Text.Contains("@RealAnalogGauge"));

            // 動作プレビュー: ats30 の欄の代わりに kmph の欄
            Check("動作プレビューに ats30 の欄はなく、入力の kmph の欄がある", _states.Range("ats30") == null && _states.Range("kmph") is { } kr && kr.Max >= 140);
            int speedo = _doc!.Sections.FindIndex(s => s.Title == "速度計");
            int press = _doc.Sections.FindIndex(s => s.Title == "圧力計");
            Check("RealAnalogGauge の針の値は入力の kmph", StateKeyOf(speedo) == "kmph");
            _states.SetValue("kmph", 20);
            RagTick(0);
            Check("プラグインと同じく ats30 = (角度 + 180) × 1000", Canvas.State.GetValueOrDefault("ats30") == 84500);
            var item = _scene!.ItemFor(speedo)!;
            Check("針は表のとおり -95.5 度を指す", Math.Abs(item.TargetAngle(Subjects.Resolve(item.Subject, Canvas.State)) - (-95.5)) < 0.01);
            _states.SetValue("kmph", 30);
            RagTick(0.05);
            double a1 = Canvas.State["ats30"];
            RagTick(0.05);
            Check("しきい値より遅いと、走るにつれて針が振動する", Canvas.State["ats30"] != a1);
            _states.SetValue("kmph", 60);
            RagTick(0.05);
            a1 = Canvas.State["ats30"];
            RagTick(0.05);
            Check("しきい値より速いと振動しない", Canvas.State["ats30"] == a1);

            // 右の欄: 表の編集
            SelectSection(speedo);
            UpdateLayout();
            var grid = RagHost.Children.OfType<DataGrid>().FirstOrDefault();
            Check("RealAnalogGauge の針を選ぶと、右の欄に表が出る（5 行）", grid != null && grid.Items.OfType<RagPoint>().Count() == 5);
            SelectRagRow(2);
            Check("表の行を選ぶと、プレビューの kmph がその値になる", Canvas.State.GetValueOrDefault("kmph") == 10);
            // 一覧の「RAG」の印
            var rows = ElementList.Items.OfType<ElementRow>().ToList();
            Check("一覧で RealAnalogGauge の針に「RAG」の印を付け、入力も出す",
                rows.Any(r => r.Index == speedo && r.RagVisibility == Visibility.Visible && r.Subject.Contains("kmph") && r.RagTip.Contains("SpeedoMeter"))
                && rows.Any(r => r.Index == press && r.RagVisibility != Visibility.Visible));
            // ゆっくり動かす
            var g30 = Rag!.ForAts(30)!;
            StartRagSweep(g30);
            Check("「0 からゆっくり動かす」で入力を 0 にして動かし始める", RagSweeping && Canvas.State.GetValueOrDefault("kmph") == 0);
            RagTick(1.0);
            RagTick(1.0);
            Check("1 秒に 2 km/h ずつ上がる", Math.Abs(Canvas.State.GetValueOrDefault("kmph") - 4) < 1e-9);
            double before = Canvas.State["ats30"];
            RagTick(1.0);
            Check("動かすと針（ats30）も動く", Canvas.State["ats30"] != before);
            for (int k = 0; k < 100; k++) RagTick(1.0);
            Check("表のいちばん大きい値（140）で止まる", !RagSweeping && Canvas.State.GetValueOrDefault("kmph") == 140);
            _ragSweepRoundTrip = true;
            StartRagSweep(g30);
            for (int k = 0; k < 75; k++) RagTick(1.0);
            Check("往復なら、上まで行ったら下る", RagSweeping && Canvas.State.GetValueOrDefault("kmph") < 140 && Canvas.State.GetValueOrDefault("kmph") > 120);
            for (int k = 0; k < 100; k++) RagTick(1.0);
            Check("往復は 0 まで戻って止まる", !RagSweeping && Canvas.State.GetValueOrDefault("kmph") == 0);
            _ragSweepRoundTrip = false;
            StartRagSweep(g30);
            for (int k = 0; k < 15; k++) RagTick(1.0);
            UpdateLayout();
            var rateBox = FindChildren<ComboBox>(RagHost).FirstOrDefault(c => c.Items.Count == 19);
            Check("速さは 0.5〜5 km/h/s（0.25 刻み）から選べる", rateBox != null && (string)rateBox.Items[0] == "0.5" && (string)rateBox.Items[1] == "0.75" && (string)rateBox.Items[18] == "5"
                && FindChildren<TextBlock>(RagHost).Any(x => x.Text == "km/h/s"));
            SaveWindowPng(this, Path.ChangeExtension(report, ".sweep.png"));
            StopRagSweep();
            SelectRagRow(2);
            double th = -100 * Math.PI / 180; // 回転の中心から -100 度の方向をクリック
            SetRagAngleFromPoint(Rag!.ForAts(30)!, new Point(150 + 50 * Math.Sin(th), 150 - 50 * Math.Cos(th)));
            log.Add($"     クリックで決めた角度: {Rag.ForAts(30)!.Points[2].Angle}");
            Check("パネル画面のクリックで、選んだ行の角度を決める（-100 度）", Math.Abs(Rag.ForAts(30)!.Points[2].Angle - (-100)) < 0.01 && Rag.ForAts(30)!.Dirty);
            // 端の行（140 km/h）を 250 度にしてから -100 度の方向をクリック → 1 周回した 260 度（今の角度に近い側）
            SelectRagRow(4);
            Rag.ForAts(30)!.Points[4].Angle = 250;
            SetRagAngleFromPoint(Rag.ForAts(30)!, new Point(150 + 50 * Math.Sin(th), 150 - 50 * Math.Cos(th)));
            log.Add($"     250 度の端の行で -100 度の方向をクリック: {Rag.ForAts(30)!.Points[4].Angle}");
            Check("1 周ずれた角度は、見込まれる側にする（250 度の行なら -100 ではなく 260）", Math.Abs(Rag.ForAts(30)!.Points[4].Angle - 260) < 0.01);
            Rag.ForAts(30)!.Points[4].Angle = 120;
            SelectRagRow(2);
            Check("まだ書いていない表は問題の欄で知らせる", ProblemList.Items.OfType<ProblemRow>().Any(r => r.Text.Contains("まだ設定ファイルに書き込んでいません")));
            SaveWindowPng(this, Path.ChangeExtension(report, ".edit.png"));
            Save(_path);
            var csv = File.ReadAllText(Path.Combine(plugin, "SpeedoMeter.csv"));
            Check("パネルを保存すると表も書く（10,-100）", csv.Contains("10,-100") && !Rag.ForAts(30)!.Dirty);
            Check("表を書くとき .bak を残す", File.Exists(Path.Combine(plugin, "SpeedoMeter.csv.bak")));
            Check("振動の値は ini に残る", File.ReadAllText(Path.Combine(plugin, "SpeedoMeter.ini")).Contains("Threshold = 52"));

            // 標準の針 → RealAnalogGauge
            _autoConfirm = _ => true;
            _ragAsk = _ => "200";
            Check("標準の針を RealAnalogGauge の針にできる", ConvertToRag(press));
            var ps = _doc!.Sections[press];
            Check("パネルの針は ats200・決まりの角度になる", ps.Get("Subject") == "ats200" && ps.Get("InitialAngle") == "-180" && ps.Get("Maximum") == "360000");
            Check("針ごとの ini・csv を作り、親の設定に Gauge1 を足す",
                File.Exists(Path.Combine(plugin, "圧力計.ini")) && File.ReadAllText(Path.Combine(plugin, "圧力計.csv")).Contains("600,120")
                && Ini.Get(Ini.Parse(File.ReadAllText(parent)), "FileName", "Gauge1") == "圧力計");
            _states.SetValue("bc", 300);
            RagTick(0);
            var pitem = _scene!.ItemFor(press)!;
            Check("変えたあとも見た目は同じ（bc 300 で 0 度）", Math.Abs(pitem.TargetAngle(Subjects.Resolve(pitem.Subject, Canvas.State))) < 0.01);
            Check("動作プレビューの欄は bc のまま（ats200 の欄は出ない）", _states.Range("ats200") == null && _states.Range("bc") != null);

            // RealAnalogGauge → 標準の針（表の最初と最後の行で直線に）
            string asked = "";
            _autoConfirm = m => { asked += m + "\n"; return true; };
            Check("RealAnalogGauge の針を標準の針に戻せる", ConvertFromRag(speedo));
            var ss = _doc!.Sections[speedo];
            Check("戻すと Subject・Minimum〜Maximum・角度は表の最初と最後の行", ss.Get("Subject") == "kmph" && ss.Get("Minimum") == "0" && ss.Get("Maximum") == "140"
                && ss.Get("InitialAngle") == "-121.5" && ss.Get("LastAngle") == "120");
            Check("直線でない表は、いちばんずれる角度を知らせる・振動がなくなることも知らせる", asked.Contains("ずれます") && asked.Contains("振動"));
            Check("Gauge0 を親の設定から外す（ini・csv は残す）", Ini.Get(Ini.Parse(File.ReadAllText(parent)), "FileName", "Gauge0") == null && File.Exists(Path.Combine(plugin, "SpeedoMeter.csv")));
            _autoConfirm = null;
            _ragAsk = null;

            // 決まりと違う角度は問題の欄で知らせる
            SelectSection(press);
            _doc.Sections[press].Set("InitialAngle", "-170");
            CommitModel();
            Check("RealAnalogGauge の針の角度が決まりと違えば知らせる", ProblemList.Items.OfType<ProblemRow>().Any(r => r.Text.Contains("決まりの値")));
            OnUndo(this, null!);

            // 手で選んだ設定はパネルファイルに書く
            UseRagIni(parent);
            Check("手で選ぶと、パネルファイルに「;@RealAnalogGauge: 相対パス」を書く", Editor.Text.Contains(@";@RealAnalogGauge: ..\Ats\Plugin\BveWorkShop\RealAnalogGauge.ini"));
            Save(_path);
            LoadPanel(panel, null);
            await Task.Delay(200);
            Check("車両なしで開いても、書いた場所から読む", Rag != null && Rag.ForAts(200) != null);

            SelectSection(_doc!.Sections.FindIndex(s => s.Title == "圧力計"));
            UpdateLayout();
            await Task.Delay(200);
            SaveWindowPng(this, Path.ChangeExtension(report, ".png"));
        }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); fail++; }
        log.Add(fail == 0 ? "ALL PASSED" : $"{fail} FAILED");
        File.WriteAllLines(report, log);
        _savedText = Editor.Text;
        if (_rag != null) foreach (var g in _rag.Gauges) g.Dirty = false;
        Close();
    }
}
