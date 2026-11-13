using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BvePanelEditor.Core;
using BvePanelEditor.Views;
using Microsoft.Win32;

namespace BvePanelEditor;

/// <summary>
/// RealAnalogGauge（BVE Workshop の ATS プラグイン。目盛が等間隔でない計器の針と、低速の針の振動）。
/// <list type="bullet">
/// <item>設定（親 ini と針ごとの ini・csv）は、車両ファイルの ATS からたどって探すか、手で選ぶ（選んだ場所はパネルファイルに「;@RealAnalogGauge: パス」で覚える）。</item>
/// <item>動作プレビュー: プラグインと同じ計算で ats に値を書く。状態量の欄は、ats ではなく入力（kmph など）の欄になる。</item>
/// <item>右の欄: 表（入力値と角度）・振動の編集、パネル画面のクリックで針の向きを決める、標準の針との相互変換。</item>
/// </list>
/// </summary>
public partial class MainWindow
{
    private RagConfig? _rag;
    /// <summary>プレビューで走った距離（m。振動の計算に使う）。</summary>
    private double _ragLocation;
    private readonly HashSet<string> _ragBackedUp = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>確認用: 入力のダイアログの代わり（質問 → 答え。null ならやめる）。</summary>
    private Func<string, string?>? _ragAsk;

    private string? _ragSignature;
    private DataGrid? _ragGrid;
    private ToggleButton? _ragPickButton;

    /// <summary>「ゆっくり動かす」: 動かしている入力のキー（止まっていれば null）・1 秒に動かす量・向き・往復するか・範囲。</summary>
    private string? _ragSweepKey;
    private double _ragSweepRate = 2, _ragSweepDir = 1, _ragSweepMin, _ragSweepMax;
    private bool _ragSweepRoundTrip;
    private Slider? _ragSlider;
    private TextBlock? _ragValueText;
    private ToggleButton? _ragSweepButton;
    private bool _ragSliderSync;

    /// <summary>今の RealAnalogGauge の設定（確認用）。</summary>
    internal RagConfig? Rag => _rag;

    // ================= 設定を探す・読む =================

    /// <summary>
    /// RealAnalogGauge の設定を読む。パネルファイルに場所が書いてあればそれ、なければ車両ファイルの ATS からたどって探す。
    /// 表をパネルくんで変えてまだ書いていない針は、変えた内容のまま残す。
    /// </summary>
    private void LoadRag(bool announce)
    {
        var dirty = _rag?.Gauges.Where(g => g.Dirty).ToList() ?? [];
        _rag = null;
        string? path = null;
        string how = "";
        if (_doc?.RagIni is { Length: > 0 } rel)
        {
            path = Path.IsPathRooted(rel) || _path == null ? rel : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_path)!, rel));
            how = "パネルファイルに書いた場所";
        }
        else if (_vehicle != null)
        {
            var found = RagLocator.Find(_vehicle.Path, [_vehicle.Ats32Path, _vehicle.Ats64Path]);
            path = found.FirstOrDefault();
            how = found.Count > 1 ? $"車両の ATS から見つけた {found.Count} 個のうち 1 つ目" : "車両の ATS から見つけた";
        }
        if (path != null)
        {
            try
            {
                _rag = RagConfig.Load(path);
                foreach (var d in dirty)
                {
                    int k = _rag.Gauges.FindIndex(g => g.IniPath.Equals(d.IniPath, StringComparison.OrdinalIgnoreCase));
                    if (k >= 0) _rag.Gauges[k] = d;
                }
                if (announce) Message($"RealAnalogGauge の設定を読みました（{how}: {path}。針 {_rag.Gauges.Count} 本）");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Message($"RealAnalogGauge の設定を読めませんでした: {ex.Message}");
            }
        }
        RagReloadMenu.IsEnabled = _rag != null;
        _ragSignature = null;
        _ragLocation = 0; // 設定を読み直したら、針の振動に使う走行距離も最初から
    }

    private void OnPickRag(object? sender, RoutedEventArgs? e)
    {
        if (_doc == null) { Message("先にパネルファイルを開いてください"); return; }
        var dlg = new OpenFileDialog
        {
            AddToRecent = false, Title = "RealAnalogGauge の設定ファイル（DLL と同じ名前の ini）を選ぶ",
            Filter = "RealAnalogGauge の設定|*.ini|すべて|*.*",
            InitialDirectory = _rag?.Dir ?? (_vehicle != null ? Path.GetDirectoryName(_vehicle.Path) : _path != null ? Path.GetDirectoryName(_path) : null),
        };
        if (dlg.ShowDialog(this) != true) return;
        UseRagIni(dlg.FileName);
    }

    /// <summary>手で選んだ親 ini を使い、パネルファイルに場所を書く。</summary>
    private void UseRagIni(string ini)
    {
        if (_doc == null) return;
        if (!RagLocator.IsParentIni(ini))
        {
            Warn($"{Path.GetFileName(ini)} は RealAnalogGauge の設定ファイル（[FileName] に Gauge0〜Gauge15 を書いた ini）ではないようです。");
            return;
        }
        FlushReparse();
        _doc.RagIni = _path != null ? Path.GetRelativePath(Path.GetDirectoryName(_path)!, Path.GetFullPath(ini)) : Path.GetFullPath(ini);
        LoadRag(announce: true);
        CommitModel();
    }

    private void OnReloadRag(object? sender, RoutedEventArgs? e)
    {
        if (_rag != null && _rag.Gauges.Any(g => g.Dirty)
            && !Confirm("RealAnalogGauge の表をパネルくんで変えて、まだ書き込んでいません。\n読み直すと、その変更は消えます。読み直しますか？"))
            return;
        if (_rag != null) foreach (var g in _rag.Gauges) g.Dirty = false;
        LoadRag(announce: true);
        RebuildAll();
    }

    // ================= 動作プレビュー =================

    /// <summary>プラグインが書く ats（atsN → 入力の状態量・表の入力値の範囲・説明）。状態量の欄は ats の代わりに入力の欄にする。</summary>
    private IReadOnlyDictionary<string, (string Input, double Min, double Max, string Note)> RagDerivedStates()
    {
        var d = new Dictionary<string, (string, double, double, string)>(StringComparer.OrdinalIgnoreCase);
        if (_rag == null) return d;
        foreach (var g in _rag.Gauges)
        {
            if (!g.HasOutput || RagGauge.StateKey(g.Subject) is not { } key) continue;
            double min = g.Points.Count > 0 ? g.Points.Min(p => p.Value) : 0, max = g.Points.Count > 0 ? g.Points.Max(p => p.Value) : 0;
            d[$"ats{g.Needle}"] = (key, min, max, $"RealAnalogGauge で ats{g.Needle} へ");
        }
        return d;
    }

    /// <summary>状態量のキー（atsN など）が RealAnalogGauge の出力なら、入力の状態量のキー。そうでなければそのまま。</summary>
    private string? RagInputKey(string? key)
    {
        if (key == null || _rag == null || !Subjects.IsAts(key, out var n)) return key;
        return _rag.ForAts(n) is { } g && RagGauge.StateKey(g.Subject) is { } input ? Subjects.BaseKey(input) ?? input : key;
    }

    /// <summary>プラグインと同じく、入力から ats に値を書く（毎フレーム）。</summary>
    private void RagTick(double dt)
    {
        if (_rag == null) return;
        if (_ragSweepKey is { } sk && dt > 0)
        {
            double v = Canvas.State.GetValueOrDefault(sk) + _ragSweepDir * _ragSweepRate * dt;
            bool stop = false;
            if (v >= _ragSweepMax)
            {
                v = _ragSweepMax;
                if (_ragSweepRoundTrip) _ragSweepDir = -1; else stop = true;
            }
            else if (v <= _ragSweepMin && _ragSweepDir < 0)
            {
                v = _ragSweepMin;
                stop = true;
            }
            _states.SetValue(sk, v);
            if (stop) StopRagSweep();
        }
        SyncRagSlider();
        _ragLocation += Math.Abs(Canvas.State.GetValueOrDefault("kmph")) / 3.6 * dt;
        bool changed = false;
        foreach (var g in _rag.Gauges)
        {
            if (!g.HasOutput || RagGauge.StateKey(g.Subject) is not { } key) continue;
            double v = g.PanelValue(Subjects.Resolve(key, Canvas.State), _ragLocation);
            var ats = $"ats{g.Needle}";
            if (Canvas.State.GetValueOrDefault(ats, double.NaN) != v) { Canvas.State[ats] = v; changed = true; }
        }
        if (changed) Canvas.InvalidateVisual();
    }

    /// <summary>入力を下から上へゆっくり動かし始める（0 から。表の入力値が負から始まるならそこから）。</summary>
    internal void StartRagSweep(RagGauge g, bool fromStart = true)
    {
        if (RagGauge.StateKey(g.Subject) is not { } key || Subjects.BaseKey(key) is not { } bk) { Message("この入力は動かせません"); return; }
        var (min, max) = RagSweepRange(g);
        _ragSweepKey = bk;
        _ragSweepMin = min;
        _ragSweepMax = max;
        _ragSweepDir = 1;
        if (fromStart || Canvas.State.GetValueOrDefault(bk) >= max) _states.SetValue(bk, Math.Max(min, Math.Min(0, max)));
        if (_ragSweepButton != null) { _ragSweepButton.IsChecked = true; _ragSweepButton.Content = "■ 止める"; }
    }

    internal void StopRagSweep()
    {
        _ragSweepKey = null;
        if (_ragSweepButton != null) { _ragSweepButton.IsChecked = false; _ragSweepButton.Content = "▶ 0 からゆっくり動かす"; }
    }

    internal bool RagSweeping => _ragSweepKey != null;

    /// <summary>動かす範囲: 0（表が負から始まるならその値）から、表のいちばん大きい入力値まで。</summary>
    private static (double Min, double Max) RagSweepRange(RagGauge g)
    {
        double lo = g.Points.Count > 0 ? g.Points.Min(p => p.Value) : 0, hi = g.Points.Count > 0 ? g.Points.Max(p => p.Value) : 100;
        if (hi <= lo) hi = lo + 100;
        return (Math.Min(0, lo), hi);
    }

    /// <summary>動かす速さの単位（速度なら km/h/s、圧力なら kPa/s、電流なら A/s）。</summary>
    private static string RagRateUnit(string key) => key switch
    {
        "kmph" => "km/h/s",
        "bc" or "mr" or "sap" or "bp" or "er" => "kPa/s",
        "am" or "amabs" => "A/s",
        _ => "/s",
    };

    /// <summary>右の欄のスライダーを、今の入力の値に合わせる。</summary>
    private void SyncRagSlider()
    {
        if (_ragSlider?.Tag is not string key) return;
        double v = Canvas.State.GetValueOrDefault(key);
        _ragSliderSync = true;
        _ragSlider.Value = Math.Clamp(v, _ragSlider.Minimum, _ragSlider.Maximum);
        _ragSliderSync = false;
        if (_ragValueText != null)
        {
            var g = CurrentRagGauge();
            var ang = g != null ? g.OutputAngle(v, _ragLocation) : 0;
            _ragValueText.Text = $"{v:0.0}（{ang:0.0} 度）";
        }
    }

    /// <summary>一覧の「RAG」の印の説明。</summary>
    private static string RagRowTip(RagGauge g) =>
        $"RealAnalogGauge（{g.Label}）が動かす針です。入力 {g.Subject} を表（{g.Points.Count} 行）で角度にして ats{g.Needle} に書きます"
        + (g.Period != 0 && g.Amplitude != 0 ? "。低速の振動あり" : "");

    /// <summary>オブジェクトが RealAnalogGauge の針なら、その設定（なければ null）。</summary>
    private RagGauge? RagOf(PanelSection s) =>
        _rag != null && s.Kind == SectionKind.Needle && Subjects.IsAts((s.Get("Subject") ?? "").Trim().ToLowerInvariant(), out var n) ? _rag.ForAts(n) : null;

    // ================= 問題の欄 =================

    private List<ProblemRow> RagProblemRows()
    {
        var rows = new List<ProblemRow>();
        if (_rag == null || _doc == null) return rows;
        rows.Add(new ProblemRow
        {
            Section = -1, InfoMark = "RAG",
            Text = $"RealAnalogGauge: 針 {_rag.Gauges.Count} 本（{Path.GetFileName(_rag.IniPath)}）",
            TipText = $"RealAnalogGauge の設定: {_rag.IniPath}\n" + string.Join("\n", _rag.Gauges.Select(g =>
                $"・{g.Label}: {g.Subject} → {(g.HasOutput ? $"ats{g.Needle}" : "（出力なし）")}、表 {g.Points.Count} 行" + (g.Period != 0 && g.Amplitude != 0 ? "、振動あり" : ""))),
        });
        foreach (var n in _rag.Notes) rows.Add(new ProblemRow { Section = -1, Text = "RealAnalogGauge: " + n });
        if (_rag.DeveloperMode)
            rows.Add(new ProblemRow { Section = -1, Text = "RealAnalogGauge: 開発者モードが有効です（公開するときは [DeveloperMode] Enabled = 0 に）" });
        foreach (var g in _rag.Gauges.Where(g => g.Dirty))
            rows.Add(new ProblemRow { Section = -1, Text = $"RealAnalogGauge: {g.Label} の表を変えて、まだ設定ファイルに書き込んでいません（保存すると書きます）" });
        for (int i = 0; i < _doc.Sections.Count; i++)
        {
            var s = _doc.Sections[i];
            if (s.Kind != SectionKind.Needle || !Subjects.IsAts((s.Get("Subject") ?? "").Trim().ToLowerInvariant(), out var n) || _rag.ForAts(n) is not { } g) continue;
            if (!IsRagAngles(s))
                rows.Add(new ProblemRow
                {
                    Section = i,
                    Text = $"#{i + 1} {DisplayName(s)}: RealAnalogGauge（{g.Label}）の針なのに、角度が決まりの値（InitialAngle -180・LastAngle 180・Minimum 0・Maximum 360000）ではありません",
                });
        }
        return rows;
    }

    private static bool IsRagAngles(PanelSection s)
    {
        static bool Is(PanelSection s, string k, double v) => Math.Abs(PanelValues.Double(s.Get(k), double.NaN) - v) < 1e-6;
        return Is(s, "InitialAngle", RagConfig.InitialAngle) && Is(s, "LastAngle", RagConfig.LastAngle)
            && Is(s, "Minimum", RagConfig.Minimum) && Is(s, "Maximum", RagConfig.Maximum);
    }

    // ================= 書き込み =================

    /// <summary>針ごとの ini と csv を書く（最初の 1 回だけ .bak を残す）。</summary>
    private void WriteRagGauge(RagGauge g)
    {
        g.Normalize();
        var iniText = File.Exists(g.IniPath) ? BveText.Read(g.IniPath).Text : RagConfig.NewGaugeIni(g.Subject, g.Needle);
        RagConfig.WriteText(g.IniPath, RagConfig.UpdateGaugeIni(iniText, g), _ragBackedUp);
        RagConfig.WriteText(g.CsvPath, g.CsvText(), _ragBackedUp);
        g.Dirty = false;
    }

    /// <summary>変えた表を全部書く。書いたものの説明（なければ ""）。</summary>
    private string WriteDirtyRag()
    {
        if (_rag == null) return "";
        var dirty = _rag.Gauges.Where(g => g.Dirty).ToList();
        if (dirty.Count == 0) return "";
        try
        {
            foreach (var g in dirty) WriteRagGauge(g);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"RealAnalogGauge の設定ファイルを書けませんでした:\n{ex.Message}");
            return "";
        }
        _ragSignature = null;
        return $"。RealAnalogGauge の表も書きました（{string.Join("、", dirty.Select(g => Path.GetFileName(g.CsvPath)))}）";
    }

    private bool ConfirmRagDiscard()
    {
        if (_rag == null || !_rag.Gauges.Any(g => g.Dirty)) return true;
        var r = _autoConfirm != null
            ? (_autoConfirm("RealAnalogGauge の表の変更を書き込みますか？") ? MessageBoxResult.Yes : MessageBoxResult.No)
            : MessageBox.Show(this, "RealAnalogGauge の表の変更を、まだ設定ファイルに書き込んでいません。書き込みますか？", "確認", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) WriteDirtyRag();
        else foreach (var g in _rag.Gauges) g.Dirty = false;
        return true;
    }

    // ================= 標準の針 ⇄ RealAnalogGauge =================

    private string? AskRag(string title, string message, string initial) =>
        _ragAsk != null ? _ragAsk(message) : InputDialog.Show(this, title, message, initial);

    /// <summary>標準の針を RealAnalogGauge の針にする（針ごとの ini・csv を作り、親 ini に GaugeN を足し、パネルの針を ats と決まりの角度にする）。</summary>
    internal bool ConvertToRag(int index)
    {
        if (_doc == null || index < 0 || index >= _doc.Sections.Count || _doc.Sections[index].Kind != SectionKind.Needle) return false;
        FlushReparse();
        if (_rag == null) { Message("先に RealAnalogGauge の設定ファイルを選んでください（ファイル → RealAnalogGauge の設定ファイルを選ぶ…）"); return false; }
        var s = _doc.Sections[index];
        var subject = (s.Get("Subject") ?? "").Trim();
        if (RagGauge.FromPanelSubject(subject) is not { } ragSubject)
        {
            Message($"Subject「{subject}」は RealAnalogGauge の入力にできません（kmph・bc・mr・sap・bp・er・am・amabs・ats0〜255）");
            return false;
        }
        int slot = _rag.FreeSlot();
        if (slot < 0) { Message("RealAnalogGauge の針は 16 本（Gauge0〜Gauge15）までです。空きがありません"); return false; }
        if (_scene?.ItemFor(index) is not { } item) return false;

        // 出力先の ats: パネル・ほかの針で使っていない番号を、大きい方から
        var used = new HashSet<int>();
        foreach (var x in _doc.Sections)
            if (Subjects.IsAts(Subjects.BaseKey(x.Get("Subject") ?? "") ?? "", out var u)) used.Add(u);
        foreach (var g in _rag.Gauges) if (g.Needle >= 0) used.Add(g.Needle);
        int suggest = Enumerable.Range(0, 256).Reverse().FirstOrDefault(n => !used.Contains(n), 255);
        var ans = AskRag("RealAnalogGauge の針にする",
            "プラグインが針の角度を書く ats の番号（0〜255）を入れてください。\nほかの ATS プラグインが使っている番号と重ならないようにしてください（初めの値は、このパネルで使っていない番号です）。",
            suggest.ToString());
        if (ans == null) return false;
        if (!int.TryParse(ans.Trim(), out var ats) || ats is < 0 or > 255) { Message("ats の番号は 0〜255 の整数で入れてください"); return false; }
        if (_rag.ForAts(ats) is { } other) { Message($"ats{ats} は RealAnalogGauge の {other.Label} が使っています。ほかの番号にしてください"); return false; }
        if (used.Contains(ats) && !Confirm($"ats{ats} は、このパネルのほかのオブジェクトが使っています。この番号にしますか？")) return false;

        var name = _rag.UniqueName(s.Title.Length > 0 ? s.Title : "Needle");
        var gauge = new RagGauge
        {
            Name = name, Slot = slot, Subject = ragSubject, Needle = ats,
            IniPath = Path.Combine(_rag.Dir, name + ".ini"), CsvPath = Path.Combine(_rag.Dir, name + ".csv"),
        };
        double min = item.Minimum, max = item.Maximum;
        if (Math.Abs(max - min) < 1e-12) max = min + 1;
        gauge.Points.Add(new RagPoint(min, item.InitialAngle));
        gauge.Points.Add(new RagPoint(max, item.LastAngle));
        gauge.Normalize();
        if (!Confirm($"「{DisplayName(s)}」を RealAnalogGauge の針にします。次のファイルを書きます。\n\n"
                + $"・{name}.ini（新しく作る。入力 {ragSubject} → ats{ats}）\n"
                + $"・{name}.csv（新しく作る。今の Minimum {Fmt(min)}〜Maximum {Fmt(max)} と InitialAngle {Fmt(item.InitialAngle)}〜LastAngle {Fmt(item.LastAngle)} の 2 行。見た目は今と同じ）\n"
                + $"・{Path.GetFileName(_rag.IniPath)}（Gauge{slot} = {name} を足す。最初だけ .bak を残す）\n"
                + $"　場所: {_rag.Dir}\n\n"
                + $"パネルの針は Subject = ats{ats}、InitialAngle -180・LastAngle 180・Minimum 0・Maximum 360000 にします。\n"
                + "ファイルへの書き込みは Ctrl+Z では戻せません（パネルの変更は戻せます）。続けますか？"))
            return false;
        try
        {
            WriteRagGauge(gauge);
            RagConfig.WriteText(_rag.IniPath, RagConfig.SetSlotLine(BveText.Read(_rag.IniPath).Text, slot, name), _ragBackedUp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"RealAnalogGauge の設定ファイルを書けませんでした:\n{ex.Message}");
            return false;
        }
        LoadRag(announce: false);
        s.Set("Subject", $"ats{ats}");
        s.Set("InitialAngle", "-180");
        s.Set("LastAngle", "180");
        s.Set("Minimum", "0");
        s.Set("Maximum", "360000");
        CommitModel();
        _props.Invalidate();
        ShowSelectionDetails(scroll: false);
        Message($"「{DisplayName(s)}」を RealAnalogGauge の針（Gauge{slot}: {name}、ats{ats}）にしました。右の欄の表で角度を合わせられます");
        return true;
    }

    /// <summary>RealAnalogGauge の針を標準の針に戻す（表の最初と最後の行で直線に）。親 ini の GaugeN を外すかも聞く。</summary>
    internal bool ConvertFromRag(int index)
    {
        if (_doc == null || _rag == null || index < 0 || index >= _doc.Sections.Count) return false;
        FlushReparse();
        var s = _doc.Sections[index];
        if (!Subjects.IsAts((s.Get("Subject") ?? "").Trim().ToLowerInvariant(), out var n) || _rag.ForAts(n) is not { } g) return false;
        if (RagGauge.StateKey(g.Subject) is not { } key) { Message($"入力「{g.Subject}」は BVE のパネルの Subject にできないので、標準の針に戻せません"); return false; }
        if (g.LinearFit() is not { } fit) { Message("表が 2 行未満なので、標準の針の角度を決められません"); return false; }
        bool vib = g.Period != 0 && g.Amplitude != 0;
        var msg = $"「{DisplayName(s)}」を標準の針に戻します。\n\nSubject = {key}、Minimum {Fmt(fit.First.Value)}〜Maximum {Fmt(fit.Last.Value)} を、InitialAngle {Fmt(fit.First.Angle)}〜LastAngle {Fmt(fit.Last.Angle)} に直線で動かします（表の最初と最後の行）。";
        if (fit.MaxError > 0.05) msg += $"\n\n表は直線ではないので、入力 {Fmt(fit.ErrorAt)} のところで、いちばん大きく {fit.MaxError:0.#} 度ずれます。";
        if (vib) msg += "\n低速の針の振動はなくなります。";
        if (!Confirm(msg + "\n\n続けますか？")) return false;
        s.Set("Subject", key);
        s.Set("Minimum", Fmt(fit.First.Value));
        s.Set("Maximum", Fmt(fit.Last.Value));
        s.Set("InitialAngle", Fmt(fit.First.Angle));
        s.Set("LastAngle", Fmt(fit.Last.Angle));
        string removed = "";
        if (g.Slot >= 0 && Confirm($"RealAnalogGauge の設定から Gauge{g.Slot}（{g.Name}）を外しますか？\n\n{Path.GetFileName(_rag.IniPath)} の「Gauge{g.Slot} = {g.Name}」の行を消します（{g.Name}.ini・.csv は残します）。\n「いいえ」なら、プラグインはこのまま ats{g.Needle} に書き続けます。"))
        {
            try
            {
                RagConfig.WriteText(_rag.IniPath, RagConfig.SetSlotLine(BveText.Read(_rag.IniPath).Text, g.Slot, null), _ragBackedUp);
                g.Dirty = false;
                removed = $"。Gauge{g.Slot} を設定から外しました";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Warn($"RealAnalogGauge の設定ファイルを書けませんでした:\n{ex.Message}");
            }
        }
        LoadRag(announce: false);
        CommitModel();
        _props.Invalidate();
        ShowSelectionDetails(scroll: false);
        Message($"「{DisplayName(s)}」を標準の針に戻しました{removed}");
        return true;
    }

    private static string Fmt(double v) => PanelValues.Num(v);

    // ================= 右の欄 =================

    /// <summary>指針を 1 つ選んでいれば、RealAnalogGauge の欄を出す（同じ針・同じ設定なら作り直さない）。</summary>
    private void ShowRag()
    {
        PanelSection? s = _doc != null && _selGroup == null && _selSet.Count <= 1 && _selected >= 0 && _selected < _doc.Sections.Count ? _doc.Sections[_selected] : null;
        if (s?.Kind != SectionKind.Needle) s = null;
        RagGauge? g = null;
        if (s != null && _rag != null && Subjects.IsAts((s.Get("Subject") ?? "").Trim().ToLowerInvariant(), out var n)) g = _rag.ForAts(n);
        var sig = s == null ? "" : $"{_selected}|{s.Get("Subject")}|{_rag?.IniPath}|{g?.IniPath}|{_rag?.Gauges.Count}";
        if (sig == _ragSignature) return;
        _ragSignature = sig;
        StopRagPick();
        StopRagSweep();
        RagHost.Children.Clear();
        _ragGrid = null;
        _ragSlider = null;
        _ragValueText = null;
        _ragSweepButton = null;
        if (s == null) return;

        RagHost.Children.Add(new Separator { Margin = new Thickness(4, 10, 4, 4) });
        RagHost.Children.Add(new TextBlock { Text = "RealAnalogGauge（針の動きの表）", FontWeight = FontWeights.Bold, Margin = new Thickness(4, 0, 4, 2) });
        if (_rag == null)
        {
            RagHost.Children.Add(Note("RealAnalogGauge の設定ファイルが見つかっていません。車両ファイルから開くと、ATS プラグインの設定から自動で探します。"));
            RagHost.Children.Add(RagButton("設定ファイルを選ぶ…", () => OnPickRag(null, null), "DLL と同じ名前の ini（[FileName] に Gauge0〜Gauge15 を書いたもの）を選びます"));
            return;
        }
        if (g == null)
        {
            RagHost.Children.Add(Note("この針は標準の動き（Minimum〜Maximum を InitialAngle〜LastAngle に直線で）です。RealAnalogGauge の針にすると、目盛が等間隔でない計器に合わせて角度を表で決められます。"));
            RagHost.Children.Add(RagButton("RealAnalogGauge の針にする…", () => ConvertToRag(_selected),
                "針ごとの ini・csv を作って、親の設定ファイルに足します。表は今の角度の 2 行から始めます（見た目は変わりません）"));
            return;
        }
        BuildRagEditor(s, g);
    }

    private void BuildRagEditor(PanelSection s, RagGauge g)
    {
        RagHost.Children.Add(new TextBlock
        {
            Text = $"{g.Label}（ats{g.Needle} に出力）", Margin = new Thickness(4, 0, 4, 4), Foreground = Brushes.DimGray,
            ToolTip = $"{g.IniPath}\n{g.CsvPath}",
        });
        if (g.EllipseDivision >= 2) RagHost.Children.Add(Note("楕円形の計器（試作中の機能）なので、パネルくんでは動きを再現しません。"));

        // 入力
        var inRow = new DockPanel { Margin = new Thickness(4, 2, 4, 2) };
        inRow.Children.Add(new TextBlock { Text = "入力", Width = 70, VerticalAlignment = VerticalAlignment.Center, ToolTip = "[SubjectKey] Subject。針が指す値" });
        var input = new ComboBox
        {
            IsEditable = true, Text = g.Subject,
            ItemsSource = new[] { "kmph", "bc", "mr", "sap", "bp", "er", "am", "amabs" },
            ToolTip = "kmph（速度）・bc・mr・sap・bp・er（圧力）・am（電流）・amabs（電流の絶対値）・panelN（ほかのプラグインの ats N）・soundN",
        };
        void CommitInput()
        {
            var v = input.Text.Trim().ToLowerInvariant();
            if (v.Length == 0 || v == g.Subject) return;
            g.Subject = v;
            RagEdited(g, rebuildStates: true);
        }
        input.LostKeyboardFocus += (_, _) => CommitInput();
        input.SelectionChanged += (_, _) => { if (input.SelectedItem is string v) { input.Text = v; CommitInput(); } };
        inRow.Children.Add(input);
        RagHost.Children.Add(inRow);

        // ゆっくり動かして確かめる
        if (RagGauge.StateKey(g.Subject) is { } simKey && Subjects.BaseKey(simKey) is { } simBase)
        {
            var (smin, smax) = RagSweepRange(g);
            var sim = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x9D, 0xC8, 0xAE)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.FromRgb(0xF1, 0xF8, 0xF3)), Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(4, 4, 4, 4),
            };
            var sp = new StackPanel();
            var head = new DockPanel();
            _ragValueText = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, ToolTip = "入力の値（針の角度）" };
            DockPanel.SetDock(_ragValueText, Dock.Right);
            head.Children.Add(_ragValueText);
            head.Children.Add(new TextBlock { Text = $"動かして確かめる（{Subjects.Describe(simKey)}）", VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(head);
            _ragSlider = new Slider { Minimum = smin, Maximum = smax, Tag = simBase, Margin = new Thickness(0, 2, 0, 2), ToolTip = "入力の値。動かすと針がその値の角度を指します（動作プレビューの欄と同じ値）" };
            _ragSlider.ValueChanged += (_, e) =>
            {
                if (_ragSliderSync) return;
                StopRagSweep();
                _states.SetValue(simBase, e.NewValue);
            };
            sp.Children.Add(_ragSlider);
            var ctl = new WrapPanel();
            _ragSweepButton = new ToggleButton
            {
                Content = "▶ 0 からゆっくり動かす", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 6, 2),
                ToolTip = "入力を 0 から表のいちばん大きい値まで、決まった速さで動かします（速度なら走った距離に合わせて低速の振動も出ます）",
            };
            _ragSweepButton.Click += (_, _) => { if (_ragSweepButton.IsChecked == true) StartRagSweep(g); else StopRagSweep(); };
            ctl.Children.Add(_ragSweepButton);
            ctl.Children.Add(new TextBlock { Text = "速さ", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 0) });
            var unit = RagRateUnit(simBase);
            var rate = new ComboBox
            {
                Width = 64, IsEditable = true, MaxDropDownHeight = 260,
                ItemsSource = Enumerable.Range(2, 19).Select(k => Fmt(k * 0.25)).ToList(), // 0.5〜5（0.25 刻み）
                Text = Fmt(_ragSweepRate), ToolTip = $"動かす速さ（{unit}）。0.5〜5 から選ぶか、数を入れます",
            };
            void SetRate()
            {
                if (double.TryParse(rate.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r) && r > 0) _ragSweepRate = r;
                else rate.Text = Fmt(_ragSweepRate);
            }
            rate.SelectionChanged += (_, _) => { if (rate.SelectedItem is string v) { rate.Text = v; SetRate(); } };
            rate.LostKeyboardFocus += (_, _) => SetRate();
            ctl.Children.Add(rate);
            ctl.Children.Add(new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 8, 0) });
            var round = new CheckBox { Content = "往復", IsChecked = _ragSweepRoundTrip, VerticalAlignment = VerticalAlignment.Center, ToolTip = "いちばん大きい値まで行ったら、0 まで戻します" };
            round.Click += (_, _) => _ragSweepRoundTrip = round.IsChecked == true;
            ctl.Children.Add(round);
            sp.Children.Add(ctl);
            sim.Child = sp;
            RagHost.Children.Add(sim);
            SyncRagSlider();
        }

        // 振動
        var vib = new WrapPanel { Margin = new Thickness(4, 2, 4, 2) };
        vib.Children.Add(new TextBlock { Text = "低速の振動", Width = 70, VerticalAlignment = VerticalAlignment.Center, ToolTip = "[Vibration]。周期か振れ幅が 0 なら振動しない" });
        vib.Children.Add(NumBox("周期", g.Period, v => g.Period = v, "10 km/h のときの振動の周期（秒）。速いほど短くなる（走った距離で揺れる）"));
        vib.Children.Add(NumBox("振れ幅", g.Amplitude, v => g.Amplitude = v, "振れ幅（入力の値で。速度が振れ幅より小さいときは速度まで）"));
        vib.Children.Add(NumBox("止める", g.Threshold, v => g.Threshold = v, "入力がこれより大きいと振動しない（0 ならいつも振動）"));
        RagHost.Children.Add(vib);

        // 表
        RagHost.Children.Add(new TextBlock
        {
            Text = "表（入力値 → 角度。角度は 12 時が 0、時計回りが正。間は直線でつなぎ、表の外は端の 2 行を延ばす）",
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(4, 6, 4, 2),
        });
        var grid = new DataGrid
        {
            ItemsSource = g.Points, AutoGenerateColumns = false, CanUserAddRows = true, CanUserDeleteRows = true, CanUserSortColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.Column, MaxHeight = 260, Margin = new Thickness(4, 0, 4, 2),
            SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow,
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "入力値", Binding = new System.Windows.Data.Binding(nameof(RagPoint.Value)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "角度（度）", Binding = new System.Windows.Data.Binding(nameof(RagPoint.Angle)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.RowEditEnding += (_, _) => Dispatcher.BeginInvoke(() => RagEdited(g, rebuildStates: true));
        grid.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Delete && !grid.IsReadOnly) Dispatcher.BeginInvoke(() => RagEdited(g, rebuildStates: true)); };
        grid.SelectionChanged += (_, _) =>
        {
            // 行を選ぶと、プレビューの入力をその値にして、針をその角度に向ける
            if (grid.SelectedItem is RagPoint p && RagGauge.StateKey(g.Subject) is { } key && Subjects.BaseKey(key) is { } bk)
            {
                _states.SetValue(bk, p.Value);
                Canvas.InvalidateVisual();
            }
        };
        _ragGrid = grid;
        RagHost.Children.Add(grid);

        // 行・角度の操作
        var ops = new WrapPanel { Margin = new Thickness(4, 2, 4, 2) };
        ops.Children.Add(SmallButton("行を足す", () =>
        {
            var sel = grid.SelectedItem as RagPoint;
            var last = g.Points.OrderBy(p => p.Value).LastOrDefault();
            var p = new RagPoint((sel ?? last)?.Value + 10 ?? 0, (sel ?? last)?.Angle ?? 0);
            g.Points.Insert(sel != null ? g.Points.IndexOf(sel) + 1 : g.Points.Count, p);
            grid.Items.Refresh();
            grid.SelectedItem = p;
            RagEdited(g, rebuildStates: true);
        }, "選んだ行の下（なければ最後）に、入力値を 10 増やした行を足します"));
        ops.Children.Add(SmallButton("行を消す", () =>
        {
            if (grid.SelectedItem is not RagPoint p) return;
            g.Points.Remove(p);
            grid.Items.Refresh();
            RagEdited(g, rebuildStates: true);
        }, "選んだ行を消します"));
        ops.Children.Add(SmallButton("入力の順に並べる", () => { g.Normalize(); grid.Items.Refresh(); RagEdited(g, rebuildStates: false); },
            "入力値の小さい順に並べ、同じ入力値の行は後のものだけ残します（書き込むときも並べます）"));
        RagHost.Children.Add(ops);

        var turn = new WrapPanel { Margin = new Thickness(4, 2, 4, 2) };
        turn.Children.Add(new TextBlock { Text = "角度", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        foreach (var d in new[] { -1.0, -0.1, 0.1, 1.0 })
            turn.Children.Add(SmallButton(d > 0 ? $"+{d}" : $"{d}", () => NudgeRagAngle(g, d), $"選んだ行の角度を {d} 度変えます"));
        _ragPickButton = new ToggleButton
        {
            Content = "パネル画面のクリックで向きを決める", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(6, 1, 6, 1),
            ToolTip = "オンの間、パネル画面をクリックすると、針の回転の中心からクリックした方向を、選んだ行の角度にします（目盛の上をクリック）。Esc かもう一度押すとやめます",
        };
        _ragPickButton.Click += (_, _) =>
        {
            if (_ragPickButton.IsChecked == true) StartRagPick(g);
            else StopRagPick();
        };
        turn.Children.Add(_ragPickButton);
        RagHost.Children.Add(turn);

        var files = new WrapPanel { Margin = new Thickness(4, 8, 4, 2) };
        files.Children.Add(SmallButton("設定ファイルに書き込む", () =>
        {
            try { WriteRagGauge(g); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warn($"書けませんでした:\n{ex.Message}"); return; }
            grid.Items.Refresh();
            RebuildAll();
            Message($"{Path.GetFileName(g.IniPath)}・{Path.GetFileName(g.CsvPath)} に書き込みました（最初だけ .bak を残します）");
        }, "針ごとの ini（入力・振動）と csv（表）に書き込みます。パネルを保存したときも書き込みます"));
        files.Children.Add(SmallButton("標準の針に戻す…", () => ConvertFromRag(_selected),
            "表の最初と最後の行で、Minimum〜Maximum・InitialAngle〜LastAngle を決めます（途中のずれは失われます）"));
        RagHost.Children.Add(files);
        RagHost.Children.Add(Note("行を選ぶと、動作プレビューの入力がその値になり、針がその角度を指します。表の変更は、パネルの保存か「設定ファイルに書き込む」でファイルに書きます（Ctrl+Z では戻せません）。"));
    }

    /// <summary>表や振動を変えた（プレビューに映し、問題の欄も直す）。</summary>
    private void RagEdited(RagGauge g, bool rebuildStates)
    {
        g.Dirty = true;
        if (rebuildStates) _states.Update(_scene);
        RagTick(0);
        Canvas.InvalidateVisual();
        RebuildAll(); // 問題の欄（まだ書いていない表）も直す。右の欄は同じ針なので作り直さない
    }

    private void NudgeRagAngle(RagGauge g, double delta)
    {
        if (_ragGrid?.SelectedItem is not RagPoint p) { Message("角度を変える行を選んでください"); return; }
        p.Angle = Math.Round(p.Angle + delta, 6);
        _ragGrid.Items.Refresh();
        _ragGrid.SelectedItem = p;
        RagEdited(g, rebuildStates: false);
    }

    private void StartRagPick(RagGauge g)
    {
        if (_ragGrid?.SelectedItem is not RagPoint)
        {
            Message("先に表の行を選んでください（その行の角度を決めます）");
            if (_ragPickButton != null) _ragPickButton.IsChecked = false;
            return;
        }
        Canvas.PickPoint = pp => SetRagAngleFromPoint(g, pp);
        Message("目盛の上をクリックすると、針の回転の中心からその方向を、選んだ行の角度にします（Esc でやめる）");
    }

    private void StopRagPick()
    {
        Canvas.PickPoint = null;
        if (_ragPickButton != null) _ragPickButton.IsChecked = false;
    }

    /// <summary>パネル画面の点（パネル座標）の方向を、選んだ行の角度にする（12 時が 0、時計回りが正。1 周ずれた角度は、前後の行から見込まれる角度（端の行なら今の角度）に近いものを選ぶ）。</summary>
    internal void SetRagAngleFromPoint(RagGauge g, Point panelPoint)
    {
        if (_ragGrid?.SelectedItem is not RagPoint p || _scene?.ItemFor(_selected) is not { } item) return;
        var q = item.Unproject(panelPoint);
        double dx = q.X - item.Location.X, dy = q.Y - item.Location.Y;
        if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9) return;
        double a = Math.Atan2(dx, -dy) * 180 / Math.PI;
        // 1 周ずれた角度のうち、見込まれる角度にいちばん近いものにする
        // （間の行なら前後の行から見込む角度、端の行なら今の角度）
        var others = new RagGauge();
        others.Points.AddRange(g.Points.Where(x => !ReferenceEquals(x, p)));
        bool inside = others.Points.Any(x => x.Value < p.Value) && others.Points.Any(x => x.Value > p.Value);
        double expect = inside ? others.Interpolate(p.Value) : p.Angle;
        a += Math.Round((expect - a) / 360) * 360;
        p.Angle = Math.Round(a, 2);
        _ragGrid.Items.Refresh();
        _ragGrid.SelectedItem = p;
        RagEdited(g, rebuildStates: false);
        Message($"入力 {Fmt(p.Value)} の角度を {Fmt(p.Angle)} 度にしました");
    }

    /// <summary>確認用: 表の行を選ぶ。</summary>
    internal void SelectRagRow(int row)
    {
        if (_ragGrid == null || _rag == null) return;
        _ragGrid.SelectedItem = _ragGrid.Items.OfType<RagPoint>().ElementAtOrDefault(row);
    }

    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(4, 2, 4, 4) };

    private static Button RagButton(string text, Action click, string tip)
    {
        var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4, 2, 4, 2), Padding = new Thickness(8, 2, 8, 2), ToolTip = tip };
        b.Click += (_, _) => click();
        return b;
    }

    private static Button SmallButton(string text, Action click, string tip)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 4, 2), Padding = new Thickness(5, 1, 5, 1), ToolTip = tip };
        b.Click += (_, _) => click();
        return b;
    }

    private FrameworkElement NumBox(string label, double value, Action<double> set, string tip)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 2), ToolTip = tip };
        p.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 0) });
        var box = new TextBox { Text = Fmt(value), Width = 46, VerticalContentAlignment = VerticalAlignment.Center };
        box.LostKeyboardFocus += (_, _) =>
        {
            if (!double.TryParse(box.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) { box.Text = Fmt(value); return; }
            if (v == value) return;
            value = v;
            set(v);
            if (CurrentRagGauge() is { } g) RagEdited(g, rebuildStates: false);
        };
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) System.Windows.Input.Keyboard.ClearFocus(); };
        p.Children.Add(box);
        return p;
    }

    /// <summary>選んでいる針の RealAnalogGauge の設定（なければ null）。</summary>
    private RagGauge? CurrentRagGauge()
    {
        if (_doc == null || _rag == null || _selected < 0 || _selected >= _doc.Sections.Count) return null;
        return Subjects.IsAts((_doc.Sections[_selected].Get("Subject") ?? "").Trim().ToLowerInvariant(), out var n) ? _rag.ForAts(n) : null;
    }
}
