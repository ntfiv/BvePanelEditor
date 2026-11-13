using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using BvePanelEditor.Core;
using BvePanelEditor.Views;

namespace BvePanelEditor;

/// <summary>画面枠の比率（いくつでも同時に描ける）と揺れの余白（表示メニュー）。選んだものは次に起動したときも使う。</summary>
public partial class MainWindow
{
    private static readonly (string Label, double W, double H)[] Aspects =
        [("16:9", 16, 9), ("16:10", 16, 10), ("4:3", 4, 3), ("5:4", 5, 4), ("3:2", 3, 2), ("21:9", 21, 9)];
    private static readonly double[] Bleeds = [0, 1, 2, 3, 5, 8];
    private const string DefaultAspects = "16:9,16:10,4:3";

    // 別ファイルの SettingsDir より先に初期化されることがあるので、使うときに組み立てる
    private static string ViewSettingsFile => Path.Combine(SettingsDir, "settings.txt");

    private void InitViewOptions()
    {
        try
        {
            if (File.Exists(ViewSettingsFile))
                foreach (var line in File.ReadAllLines(ViewSettingsFile))
                {
                    var kv = line.Split('=', 2);
                    if (kv.Length != 2) continue;
                    // 前の版の「Aspect=16:9」（1 つだけ）は読まず、既定の 16:9・16:10・4:3 を同時に描く
                    if (kv[0] == "Aspects") SetAspects(kv[1], save: false);
                    else if (kv[0] == "Bleed" && PanelValues.TryDouble(kv[1], out var b)) SetBleed(b, save: false);
                    else if (kv[0] == "ListOrder") SetListOrder(kv[1] == "layer", save: false);
                    else if (kv[0] == "CompactList") SetCompactList(kv[1] == "1", save: false);
                    else if (kv[0] == "ProblemsFolded") SetProblemsFolded(kv[1] == "1", save: false);
                }
        }
        catch (IOException) { }
        BuildViewMenus();
    }

    private void SaveViewSettings()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllLines(ViewSettingsFile,
                [$"Aspects={string.Join(",", Canvas.FrameAspects.Select(f => f.Label))}", $"Bleed={PanelValues.Num(Canvas.ShakeMarginPercent)}", $"ListOrder={(_layerOrder ? "layer" : "file")}",
                 $"CompactList={(_compactList ? 1 : 0)}", $"ProblemsFolded={(_problemsFolded ? 1 : 0)}"]);
        }
        catch (IOException) { }
    }

    // ---- オブジェクトの一覧の 1 行表示・問題の欄を畳む ----

    private bool _compactList;
    private bool _problemsFolded;
    private GridLength _problemHeight = new(160);

    private void OnCompactList(object sender, RoutedEventArgs e) => SetCompactList(CompactListCheck.IsChecked == true, save: true);

    /// <summary>オブジェクトの一覧を 1 行表示にする・戻す（次に起動したときも使う）。</summary>
    public void SetCompactList(bool compact, bool save)
    {
        _compactList = compact;
        CompactListCheck.IsChecked = compact;
        if (save) SaveViewSettings();
        if (_doc != null) RebuildAll();
    }

    private System.Windows.Point _problemDragStart;
    private double _problemDragHeight;
    private bool _problemDragging;

    /// <summary>問題の見出し: 押した位置を覚える（ドラッグで高さを変え、動かさなければクリックとして畳む／開く）。</summary>
    private void OnProblemHeaderDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _problemDragStart = e.GetPosition(this);
        _problemDragHeight = ProblemRowDef.Height.IsAbsolute ? ProblemRowDef.Height.Value : ProblemRowDef.ActualHeight;
        _problemDragging = false;
        (sender as System.Windows.UIElement)?.CaptureMouse();
    }

    private void OnProblemHeaderMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || sender is not System.Windows.UIElement el || !el.IsMouseCaptured) return;
        var dy = e.GetPosition(this).Y - _problemDragStart.Y;
        if (!_problemDragging && Math.Abs(dy) < 4) return;
        if (_problemsFolded) return; // 畳んでいるときは、クリックで開くだけ
        _problemDragging = true;
        ProblemRowDef.Height = new GridLength(Math.Clamp(_problemDragHeight - dy, 48, Math.Max(120, ActualHeight - 260)));
    }

    private void OnProblemHeaderUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        (sender as System.Windows.UIElement)?.ReleaseMouseCapture();
        if (_problemDragging) { if (ProblemRowDef.Height.IsAbsolute) _problemHeight = ProblemRowDef.Height; }
        else SetProblemsFolded(!_problemsFolded, save: true);
        _problemDragging = false;
    }

    /// <summary>問題の欄を畳む・開く（次に起動したときも使う）。畳んでいても、問題があれば見出しの横に印を出す。</summary>
    public void SetProblemsFolded(bool folded, bool save)
    {
        if (folded == _problemsFolded && ProblemList.Visibility == (folded ? Visibility.Collapsed : Visibility.Visible)) return;
        if (folded && ProblemRowDef.Height.IsAbsolute) _problemHeight = ProblemRowDef.Height;
        _problemsFolded = folded;
        ProblemList.Visibility = folded ? Visibility.Collapsed : Visibility.Visible;
        ProblemFold.Text = folded ? "▶" : "▼";
        ProblemRowDef.Height = folded ? GridLength.Auto : _problemHeight;
        ProblemSplitterRow.Height = new GridLength(folded ? 0 : 5);
        if (save) SaveViewSettings();
    }

    private void BuildViewMenus()
    {
        AspectMenu.Items.Clear();
        foreach (var (label, _, _) in Aspects)
        {
            var mi = new MenuItem { Header = label, IsCheckable = true, IsChecked = Canvas.FrameAspects.Any(f => f.Label == label), StaysOpenOnClick = true };
            var l = label;
            mi.Click += (_, _) => ToggleAspect(l);
            AspectMenu.Items.Add(mi);
        }
        // 自分で足した比率
        foreach (var (label, _) in Canvas.FrameAspects.Where(f => !Aspects.Any(a => a.Label == f.Label)).ToList())
        {
            var mi = new MenuItem { Header = label, IsCheckable = true, IsChecked = true, StaysOpenOnClick = true };
            var l = label;
            mi.Click += (_, _) => ToggleAspect(l);
            AspectMenu.Items.Add(mi);
        }
        AspectMenu.Items.Add(new Separator());
        var other = new MenuItem { Header = "ほかの比率を足す…" };
        other.Click += (_, _) =>
        {
            var s = InputDialog.Show(this, "画面枠の比率", "横:縦 で入力してください（例: 1920:1080、17:9）", "");
            if (s == null) return;
            if (ParseAspect(s) is not { } a) { Warn("「横:縦」の形で入力してください（例: 16:9）"); return; }
            if (!Canvas.FrameAspects.Any(f => f.Label == a.Label)) Canvas.FrameAspects.Add(a);
            AfterAspectsChanged($"画面枠 {a.Label} を足しました");
        };
        AspectMenu.Items.Add(other);
        var reset = new MenuItem { Header = "16:9・16:10・4:3 に戻す" };
        reset.Click += (_, _) => { SetAspects(DefaultAspects, save: false); AfterAspectsChanged("画面枠を 16:9・16:10・4:3 にしました"); };
        AspectMenu.Items.Add(reset);

        BleedMenu.Items.Clear();
        bool known = false;
        foreach (var b in Bleeds)
        {
            bool on = Math.Abs(b - Canvas.ShakeMarginPercent) < 1e-9;
            known |= on;
            var mi = new MenuItem { Header = b == 0 ? "なし" : $"{PanelValues.Num(b)}%", IsCheckable = true, IsChecked = on };
            var v = b;
            mi.Click += (_, _) => SetBleed(v, save: true);
            BleedMenu.Items.Add(mi);
        }
        var custom = new MenuItem { Header = known ? "その他…" : $"その他（{PanelValues.Num(Canvas.ShakeMarginPercent)}%）…", IsCheckable = true, IsChecked = !known };
        custom.Click += (_, _) =>
        {
            var s = InputDialog.Show(this, "揺れの余白", "画面の横幅に対する % で入力してください（例: 4）", PanelValues.Num(Canvas.ShakeMarginPercent));
            if (s != null)
            {
                if (PanelValues.TryDouble(s.TrimEnd('%', '％', ' '), out var v) && v is >= 0 and <= 50) SetBleed(v, save: true);
                else Warn("0〜50 の数で入力してください");
            }
            BuildViewMenus();
        };
        BleedMenu.Items.Add(custom);
    }

    /// <summary>画面枠を 1 つ出す・消す。</summary>
    private void ToggleAspect(string label)
    {
        int i = Canvas.FrameAspects.FindIndex(f => f.Label == label);
        if (i >= 0) Canvas.FrameAspects.RemoveAt(i);
        else if (ParseAspect(label) is { } a) Canvas.FrameAspects.Add(a);
        AfterAspectsChanged(Canvas.FrameAspects.Count == 0 ? "画面枠を消しました" : $"画面枠: {string.Join("・", Canvas.FrameAspects.Select(f => f.Label))}");
    }

    private void AfterAspectsChanged(string message)
    {
        Canvas.InvalidateVisual();
        SaveViewSettings();
        BuildViewMenus();
        Message(message);
    }

    /// <summary>"16:9" や "1920:1080" を比率にする。読めなければ null。</summary>
    private static (string Label, double Aspect)? ParseAspect(string text)
    {
        var m = Regex.Match(text.Trim(), @"^(\d+(?:\.\d+)?)\s*[:：x×/]\s*(\d+(?:\.\d+)?)$");
        if (!m.Success || !PanelValues.TryDouble(m.Groups[1].Value, out var w) || !PanelValues.TryDouble(m.Groups[2].Value, out var h) || w <= 0 || h <= 0)
            return null;
        return ($"{PanelValues.Num(w)}:{PanelValues.Num(h)}", w / h);
    }

    /// <summary>"16:9,16:10,4:3" のように並べた比率を、同時に描く画面枠にする。空なら枠なし。</summary>
    private void SetAspects(string text, bool save)
    {
        var list = new List<(string Label, double Aspect)>();
        foreach (var part in text.Split([',', '、', ' '], StringSplitOptions.RemoveEmptyEntries))
            if (ParseAspect(part) is { } a && !list.Any(x => x.Label == a.Label)) list.Add(a);
        Canvas.FrameAspects = list;
        Canvas.InvalidateVisual();
        if (save) { SaveViewSettings(); BuildViewMenus(); }
    }

    private void SetBleed(double percent, bool save)
    {
        Canvas.ShakeMarginPercent = percent;
        Canvas.InvalidateVisual();
        if (save)
        {
            SaveViewSettings();
            BuildViewMenus();
            Message(percent > 0 ? $"揺れの余白を画面の横幅の {PanelValues.Num(percent)}% にしました（桃色の点線）" : "揺れの余白を消しました");
        }
    }
}
