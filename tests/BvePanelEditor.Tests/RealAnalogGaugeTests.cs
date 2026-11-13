using BvePanelEditor.Core;
using Xunit;

namespace BvePanelEditor.Tests;

/// <summary>RealAnalogGauge の設定の読み書きと、プラグインと同じ計算。</summary>
public class RealAnalogGaugeTests
{
    private const string SpeedCsv = "0,-121.5\r\n5,-115\r\n10,-110.5\r\n20,-95.5\r\n140,120\r\n";

    private static RagGauge Gauge(string csv)
    {
        var g = new RagGauge();
        g.Points.AddRange(RagGauge.ParseCsv(csv));
        g.Normalize();
        return g;
    }

    [Fact]
    public void Interpolate_matches_plugin()
    {
        var g = Gauge(SpeedCsv);
        Assert.Equal(-121.5, g.Interpolate(0), 6);
        Assert.Equal(-113.0 + 0.25, g.Interpolate(7.5), 6);   // 5〜10 の真ん中: -115 と -110.5 の真ん中
        Assert.Equal(-95.5, g.Interpolate(20), 6);
        Assert.Equal(120, g.Interpolate(140), 6);
        // 表の外は端の 2 点を延ばす
        Assert.Equal(-121.5 - 6.5, g.Interpolate(-5), 6);
        Assert.Equal(120 + (215.5 / 120) * 10, g.Interpolate(150), 6);
    }

    [Fact]
    public void Interpolate_needs_two_rows()
    {
        Assert.Equal(0, Gauge("10,50\r\n").Interpolate(10));
        Assert.Equal(0, Gauge("").Interpolate(10));
    }

    [Fact]
    public void Csv_skips_non_numbers_and_later_rows_win()
    {
        var g = Gauge("入力,角度\r\n0,10\r\nabc\r\n0,20\r\n10,30\r\n");
        Assert.Equal(2, g.Points.Count);
        Assert.Equal(20, g.Points[0].Angle);
    }

    [Fact]
    public void PanelValue_is_angle_plus_180_times_1000()
    {
        var g = Gauge(SpeedCsv);
        Assert.Equal((-121.5 + 180) * 1000, g.PanelValue(0, 0));
        // パネルの針（-180〜180 度・0〜360000）で戻すと同じ角度
        double v = g.PanelValue(20, 0);
        Assert.Equal(-95.5, RagConfig.InitialAngle + v / RagConfig.Maximum * (RagConfig.LastAngle - RagConfig.InitialAngle), 3);
    }

    [Fact]
    public void Vibrate_matches_plugin()
    {
        var g = Gauge(SpeedCsv);
        g.Period = 0.5; g.Amplitude = 10; g.Threshold = 52;
        double coef = 2 * Math.PI / (10.0 * 1000 / 3600 * 0.5);
        Assert.Equal(30 + Math.Sin(3 * coef) * 10, g.Vibrate(30, 3), 9);
        Assert.Equal(4 + Math.Sin(3 * coef) * 4, g.Vibrate(4, 3), 9);     // 振れ幅は速度まで
        Assert.Equal(60, g.Vibrate(60, 3));                                 // しきい値より速ければ振動しない
        Assert.Equal(60, g.Vibrate(-60, 3));                                // 絶対値になる
        g.Amplitude = 0;
        Assert.Equal(-60, g.Vibrate(-60, 3));                               // 振動なしなら元のまま
    }

    [Fact]
    public void LinearFit_reports_largest_error()
    {
        var fit = Gauge("0,-120\r\n50,10\r\n100,120\r\n").LinearFit();
        Assert.NotNull(fit);
        Assert.Equal(0, fit.Value.First.Value);
        Assert.Equal(120, fit.Value.Last.Angle);
        Assert.Equal(10, fit.Value.MaxError, 6);
        Assert.Equal(50, fit.Value.ErrorAt);
    }

    [Theory]
    [InlineData("kmph", "kmph")]
    [InlineData("panel12", "ats12")]
    [InlineData("amabs", "amabs")]
    [InlineData("sound3", null)]
    [InlineData("speed", null)]
    public void StateKey_maps_plugin_subjects(string subject, string? key) => Assert.Equal(key, RagGauge.StateKey(subject));

    [Theory]
    [InlineData("kmph", "kmph")]
    [InlineData("ats40", "panel40")]
    [InlineData("hour", null)]
    public void FromPanelSubject_maps_panel_subjects(string subject, string? key) => Assert.Equal(key, RagGauge.FromPanelSubject(subject));

    [Fact]
    public void Ini_set_keeps_other_lines()
    {
        var text = "[FileName]\r\nGauge0 = SpeedoMeter\r\n\r\n[DeveloperMode]\r\nEnabled = 0\r\n";
        var t = Ini.Set(text, "FileName", "Gauge1", "Ammeter");
        Assert.Equal("[FileName]\r\nGauge0 = SpeedoMeter\r\nGauge1 = Ammeter\r\n\r\n[DeveloperMode]\r\nEnabled = 0\r\n", t);
        t = Ini.Set(t, "developermode", "ENABLED", "1");
        Assert.Contains("Enabled = 1", t);
        t = Ini.Set(t, "Vibration", "Period", "0.5");
        Assert.EndsWith("Enabled = 1\r\n\r\n[Vibration]\r\nPeriod = 0.5\r\n", t);
        t = Ini.Remove(t, "FileName", "Gauge0");
        Assert.DoesNotContain("SpeedoMeter", t);
        Assert.Equal("Ammeter", Ini.Get(Ini.Parse(t), "FileName", "Gauge1"));
    }

    [Fact]
    public void Counterpart_matches_by_name_and_copy_syncs_content()
    {
        var root = Path.Combine(Path.GetTempPath(), "ragcp_" + Guid.NewGuid().ToString("N"));
        try
        {
            var d1 = Path.Combine(root, "x86"); var d2 = Path.Combine(root, "x64");
            Directory.CreateDirectory(d1); Directory.CreateDirectory(d2);
            foreach (var d in new[] { d1, d2 })
            {
                File.WriteAllText(Path.Combine(d, "RealAnalogGauge.ini"), "[FileName]\r\nGauge0 = SpeedoMeter\r\n");
                File.WriteAllText(Path.Combine(d, "SpeedoMeter.ini"), "[SubjectKey]\r\nSubject = kmph\r\n\r\n[PanelIndex]\r\nNeedle = 30\r\n");
                File.WriteAllText(Path.Combine(d, "SpeedoMeter.csv"), SpeedCsv);
            }
            var c1 = RagConfig.Load(Path.Combine(d1, "RealAnalogGauge.ini"));
            var c2 = RagConfig.Load(Path.Combine(d2, "RealAnalogGauge.ini"));
            var g = c1.ForAts(30)!;
            var cp = c2.Counterpart(g);
            Assert.NotNull(cp);
            Assert.True(g.SameContent(cp!));
            Assert.Null(c2.Counterpart(new RagGauge { Name = "Ammeter", Slot = 1 }));
            g.Points[2].Angle = -100;
            Assert.False(g.SameContent(cp));                 // 片方だけ変えると食い違う
            g.CopyContentTo(cp);
            Assert.True(g.SameContent(cp));                  // 写せばそろう
            cp!.Points[0].Angle = 999;
            Assert.NotEqual(999, g.Points[0].Angle);         // 表は写し（同じ行を共有しない）
            var shuffled = new RagGauge { Subject = "kmph", Needle = 30 };
            shuffled.Points.AddRange(g.Points.AsEnumerable().Reverse());
            Assert.True(g.SameContent(shuffled));            // 並びが違っても、並べ直せば同じ
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public void Load_and_locate_through_plugin_organizer()
    {
        var root = Path.Combine(Path.GetTempPath(), "rag_" + Guid.NewGuid().ToString("N"));
        try
        {
            var plugin = Path.Combine(root, "Ats", "Plugin", "BveWorkShop");
            var org = Path.Combine(root, "Ats", "Organizer");
            Directory.CreateDirectory(plugin);
            Directory.CreateDirectory(org);
            File.WriteAllText(Path.Combine(plugin, "RealAnalogGauge.dll"), "");
            File.WriteAllText(Path.Combine(plugin, "RealAnalogGauge.ini"), "[FileName]\r\nGauge0 = SpeedoMeter\r\nGauge2 = Missing\r\n\r\n[DeveloperMode]\r\nEnabled = 1\r\n");
            File.WriteAllText(Path.Combine(plugin, "SpeedoMeter.ini"), "[SubjectKey]\r\nSubject = kmph\r\n\r\n[PanelIndex]\r\nNeedle = 30\r\n\r\n[Vibration]\r\nPeriod = 0.5\r\nAmplitude = 10\r\nThreshold = 52\r\n");
            File.WriteAllText(Path.Combine(plugin, "SpeedoMeter.csv"), SpeedCsv);
            File.WriteAllText(Path.Combine(org, "PluginOrganizer.dll"), "");
            File.WriteAllText(Path.Combine(org, "Instructions.yml"), "version: 1.0.00\r\npip:\r\n  - prio: 0\r\n    exec:\r\n      - run ATS_S.dll\r\n      - run ..\\Plugin\\BveWorkShop\\RealAnalogGauge.dll\r\n");
            var vehicle = Path.Combine(root, "Vehicle.txt");
            File.WriteAllText(vehicle, "BveTs Vehicle 2.00\r\nAts = Ats\\Organizer\\PluginOrganizer.dll\r\n");

            var found = RagLocator.Find(vehicle, [Path.Combine(org, "PluginOrganizer.dll")]);
            Assert.Equal([Path.Combine(plugin, "RealAnalogGauge.ini")], found);

            var cfg = RagConfig.Load(found[0]);
            Assert.True(cfg.DeveloperMode);
            Assert.Single(cfg.Gauges);
            var g = cfg.ForAts(30);
            Assert.NotNull(g);
            Assert.Equal("kmph", g!.Subject);
            Assert.Equal(0.5, g.Period);
            Assert.Equal(5, g.Points.Count);
            Assert.Contains(cfg.Notes, n => n.Contains("Missing"));
            Assert.Equal(1, cfg.FreeSlot());
            Assert.Equal("SpeedoMeter_2", cfg.UniqueName("SpeedoMeter"));

            // 書き戻し: ほかの行を残して値だけ変え、最初の 1 回だけ .bak を残す
            g.Threshold = 40;
            var path = g.IniPath;
            RagConfig.WriteText(path, RagConfig.UpdateGaugeIni(File.ReadAllText(path), g));
            Assert.True(File.Exists(path + ".bak"));
            var again = RagConfig.Load(found[0]).ForAts(30)!;
            Assert.Equal(40, again.Threshold);
            Assert.Equal(10, again.Amplitude);

            // 何も書いていなければ、車両のフォルダの下から探す
            Assert.Equal(found, RagLocator.Find(vehicle, []));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
