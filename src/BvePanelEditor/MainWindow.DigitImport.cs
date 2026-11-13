using System.IO;
using System.Windows;
using BvePanelEditor.Core;
using BvePanelEditor.Core.Crop;
using BvePanelEditor.Core.Psd;
using BvePanelEditor.Rendering;
using BvePanelEditor.Views;

namespace BvePanelEditor;

/// <summary>
/// パネルを開いているときに画像・PSD を読み込む: 常点灯の画像（表示灯）か、デジタル数字（コマ割済／単コマ）として置く。
/// </summary>
public partial class MainWindow
{
    /// <summary>デジタル数字の状態量の初期値（テスト画面で直せる）。点灯を確かめる仮の状態量と同じ。</summary>
    public const string DigitDefaultSubject = ImagePanelWindow.TemporarySubject;

    private static bool IsPsdFile(string f) => Path.GetExtension(f).Equals(".psd", StringComparison.OrdinalIgnoreCase);

    /// <summary>読み込んだ画像・PSD をどう使うか聞いて、今のパネルに置く。</summary>
    /// <summary>
    /// 追加メニューの「画像を読み込む…」: 画像・PSD を選んで、ドロップしたときと同じく読み込む（常点灯画像・コマ割済・単コマを聞く）。
    /// 置く場所は、パネル画面の真ん中。
    /// </summary>
    private void OnAddImages(object? sender, RoutedEventArgs? e)
    {
        if (_doc == null) { Message("先にパネルファイルを開くか、新規パネルを作ってください"); return; }
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            AddToRecent = false, Multiselect = true, Title = "読み込む画像・PSD",
            Filter = "画像・PSD|" + string.Join(";", ImageExtensions.Append(".psd").Select(x => "*" + x)) + "|すべて|*.*",
        };
        if (PanelDir != null) dlg.InitialDirectory = PanelDir;
        if (dlg.ShowDialog(this) != true) return;
        var files = dlg.FileNames.Where(f => IsImageFile(f) || IsPsdFile(f)).ToList();
        if (files.Count == 0) { Message("画像・PSD を選んでください"); return; }
        ImportImagesToPanel(files, null);
    }

    public void ImportImagesToPanel(IReadOnlyList<string> files, Point? at)
    {
        var name = Path.GetFileName(files[0]) + (files.Count > 1 ? $" ほか {files.Count - 1} 個" : "");
        int pick = ChoiceDialog.Show(this, "画像を読み込みます", $"「{name}」をどう読み込みますか？",
        [
            "常点灯画像として読み込む（表示灯・Subject = true）",
            "DigitalNumber として読み込む（コマ割済）… BVE 用にコマを縦に並べてある画像",
            "DigitalNumber として読み込む（単コマ）… 1 コマずつの画像から、コマ割を作る",
        ]);
        switch (pick)
        {
            case 0: ImportAsLamps(files, at); break;
            case 1:
                for (int k = 0; k < files.Count; k++)
                    if (!ImportFramedDigit(files[k], OffsetAt(at, k))) break;
                break;
            case 2: ImportSingleFrames(files, at); break;
        }
    }

    private Point OffsetAt(Point? at, int k)
    {
        var c = at ?? Canvas.ViewCenterPanel;
        return new Point(c.X + k * 20, c.Y + k * 20);
    }

    private int NextLayer() => (_doc?.Sections.Where(s => s.Kind is SectionKind.PilotLamp or SectionKind.Needle or SectionKind.DigitalNumber or SectionKind.DigitalGauge)
        .Select(s => (int)Math.Round(PanelValues.Double(s.Get("Layer"), 0))).DefaultIfEmpty(0).Max() ?? 0) + 1;

    // ================= 常点灯画像 =================

    /// <summary>常点灯の表示灯として置く。画像はそのまま、PSD は使うレイヤーを選んで 1 枚に重ね、PNG に書き出す。</summary>
    private void ImportAsLamps(IReadOnlyList<string> files, Point? at)
    {
        var images = files.Where(f => !IsPsdFile(f)).ToList();
        if (images.Count > 0) PlaceImageParts(images, SectionKind.PilotLamp, at);
        int k = images.Count;
        foreach (var psd in files.Where(IsPsdFile))
        {
            if (PanelDir == null) { Warn("先にパネルファイルを保存してください（画像をパネルからの場所で指すため）。"); return; }
            if (PickPsdLayers(psd, "常点灯画像") is not { } pick) continue;
            // PSD がメインパネルと同じ大きさなら、絵のある所の位置のまま置く。そうでなければ、置いた所を中心に
            var bounds = DigitStrip.ContentBounds(pick.Night != null ? Union(pick.Day, pick.Night) : pick.Day);
            if (bounds.IsEmpty) { Warn($"「{Path.GetFileName(psd)}」の選んだレイヤーに絵がありません。"); continue; }
            var day = RegionDetect.CropOf(pick.Day, bounds);
            var night = pick.Night != null ? RegionDetect.CropOf(pick.Night, bounds) : null;
            var name = Path.GetFileNameWithoutExtension(psd);
            var (dayRel, nightRel) = WritePanelPng("parts", name, day, night, pow2: true);
            bool samePlace = _doc?.MainPanel is { } main && MainRect(main) is { } mr && mr.W == pick.Day.Width && mr.H == pick.Day.Height;
            var c = OffsetAt(at, k++);
            var loc = samePlace ? new Point(bounds.Left, bounds.Top) : new Point(Math.Round(c.X - day.Width / 2.0), Math.Round(c.Y - day.Height / 2.0));
            var sec = PanelSection.Create(SectionKind.PilotLamp);
            sec.SetTitle(name);
            sec.Set("Subject", "true");
            sec.Set("Location", PanelValues.Point(loc.X, loc.Y));
            sec.Set("DaytimeImage", dayRel);
            if (nightRel != null) sec.Set("NighttimeImage", nightRel);
            sec.Set("Layer", NextLayer().ToString());
            AddImportedSections([sec], $"常点灯画像「{name}」を置きました（{dayRel}）。");
        }
    }

    private static PsdPixels Union(PsdPixels a, PsdPixels b)
    {
        // 絵のある範囲を調べるため、昼・夜のどちらかに絵がある所を不透明にした絵
        var px = (byte[])a.Bgra.Clone();
        for (int i = 3; i < px.Length && i < b.Bgra.Length; i += 4) px[i] = Math.Max(px[i], b.Bgra[i]);
        return new PsdPixels(a.Bounds, px);
    }

    /// <summary>PSD の使うレイヤーを選ぶ（昼・夜）。使えるレイヤーが 1 枚ならそのまま。やめたら null。</summary>
    private (PsdPixels Day, PsdPixels? Night, List<string> Layers)? PickPsdLayers(string path, string subject)
    {
        try
        {
            using (var psd = PsdFile.Open(path))
            {
                var layers = psd.ComposableLayers.ToList();
                if (layers.Count == 0) { Warn($"「{Path.GetFileName(path)}」に使えるレイヤーがありません。"); return null; }
                if (layers.Count == 1)
                    return (PsdCompose.Compose(psd, layers, psd.Canvas), null, [layers[0].Path]);
            }
            var w = new LayerAssignWindow(this, [path], ["昼の画像", "夜の画像"], [new(false, false), new(true, false)],
                [path, path], [null, null], $"{subject}「{Path.GetFileName(path)}」");
            LayerPick?[]? res;
            if (LayerAssignAnswer != null) { if (!LayerAssignAnswer(w)) return null; res = w.PickForTest(); }
            else { if (w.ShowDialog() != true) return null; res = w.Result; }
            if (res?[0] is not { } day) return null;
            return (day.Pixels, res[1]?.Pixels, day.Layers);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            Warn($"「{Path.GetFileName(path)}」を読めませんでした:\n{ex.Message}");
            return null;
        }
    }

    /// <summary>確認用: 設定するとレイヤーを選ぶ画面を出さずにこの関数に渡す（true で OK）。</summary>
    public static Func<LayerAssignWindow, bool>? LayerAssignAnswer;

    // ================= デジタル数字（コマ割済） =================

    /// <summary>
    /// BVE 用にコマを縦に並べてある画像（PSD は使うレイヤーを選ぶ）を、デジタル数字として置く。
    /// BVE のパネル用の大きさ（2 の累乗）でなければ、左上に詰めて透明で埋めるか聞く。1 コマの高さは画像から求め、テスト画面で確かめる。
    /// 続けて読み込むなら true（やめたら false）。
    /// </summary>
    public bool ImportFramedDigit(string path, Point at)
    {
        if (_doc == null) return false;
        if (PanelDir == null) { Warn("先にパネルファイルを保存してください（画像をパネルからの場所で指すため）。"); return false; }
        FlushReparse();
        var name = Path.GetFileNameWithoutExtension(path);
        PsdPixels day;
        PsdPixels? night = null;
        string? nightFile = null;
        bool fromPsd = IsPsdFile(path);
        int contentH;
        if (fromPsd)
        {
            if (PickPsdLayers(path, "デジタル数字（コマ割済）") is not { } pick) return false;
            day = pick.Day; night = pick.Night;
            contentH = day.Height;
        }
        else
        {
            var loaded = ImageLoader.TryLoadPixels(path);
            if (loaded == null) { Warn($"「{Path.GetFileName(path)}」を読めませんでした。"); return false; }
            day = loaded;
            nightFile = NeedlePickerWindow.FindNight(path);
            night = nightFile != null ? ImageLoader.TryLoadPixels(nightFile) : null;
            if (night == null) nightFile = null;
            contentH = PngWriter.TryReadContentSize(path, out _, out var ch) ? ch : day.Height;
        }

        // BVE のパネル用の大きさ（幅・高さとも 2 の累乗）か
        bool fix = false;
        if (!DigitStrip.IsPanelSize(day.Width, day.Height))
        {
            int pw = PngWriter.NextPow2(day.Width), ph = PngWriter.NextPow2(day.Height);
            int answer = ChoiceDialog.Show(this, "画像の大きさ",
                $"「{Path.GetFileName(path)}」の大きさ {day.Width}×{day.Height} は、BVE のパネル用の大きさ（幅・高さとも 2 の累乗）ではありません。\n"
                + $"左上に詰めて透明で埋めた {pw}×{ph} の画像を作りますか？（元の画像は書き換えません）",
                [$"直した画像（{pw}×{ph}）を作って使う", "このまま使う", "やめる"]);
            if (answer is < 0 or 2) return false;
            fix = answer == 0;
        }

        // 1 コマの高さは画像から求め、テスト画面で確かめる
        var found = DigitalFrames.Detect(day, contentH);
        int interval = found?.Interval ?? (contentH >= 10 ? contentH / 10 : contentH);
        var note = found == null ? "画像から 1 コマの高さを求められなかったので、仮に 10 コマとしています" : $"画像から求めた値です（{(found.Confident ? "確定" : "推定")}: {found.Reason}）";
        var test = new DigitTestWindow(this, name, day, night, contentH, interval, note, DigitDefaultSubject,
            allowNightPick: !fromPsd, nightName: nightFile != null ? Path.GetFileName(nightFile) : fromPsd && night != null ? "PSD の夜のレイヤー" : null);
        if (!test.Ask()) return false;
        if (test.NightChanged) { night = test.Night; nightFile = test.NightPath; }

        string dayRel;
        string? nightRel;
        try
        {
            if (fromPsd || fix)
                (dayRel, nightRel) = WritePanelPng("digit", name, day, night, pow2: fix);
            else
            {
                var copied = new List<string>();
                dayRel = ImageForPanel(Path.GetFullPath(path), copied);
                nightRel = nightFile != null ? ImageForPanel(nightFile, copied) : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"画像を書き出せませんでした:\n{ex.Message}");
            return false;
        }
        PlaceDigit(name, dayRel, nightRel, day.Width, test.Interval, test.Subject, at,
            (fix ? $"画像を {PngWriter.NextPow2(day.Width)}×{PngWriter.NextPow2(day.Height)} に直して書き出しました（{dayRel}）。" : fromPsd ? $"画像を書き出しました（{dayRel}）。" : ""));
        return true;
    }

    // ================= デジタル数字（単コマ） =================

    /// <summary>1 コマずつの画像・PSD から、コマ割を作る画面を開き、テスト画面で確かめてデジタル数字として置く。</summary>
    public void ImportSingleFrames(IReadOnlyList<string> files, Point? at)
    {
        if (_doc == null) return;
        if (PanelDir == null) { Warn("先にパネルファイルを保存してください（画像をパネルからの場所で指すため）。"); return; }
        FlushReparse();
        // 名前の初期値は、並べたときの最初の画像の名前（連番なら末尾の数を外す: ID_motor_0 → ID_motor）
        var name = Path.GetFileNameWithoutExtension(files.OrderBy(f => f, NaturalComparer.Instance).First());
        var stem = System.Text.RegularExpressions.Regex.Replace(name, @"[_\- ]*\d+$", "");
        if (stem.Length > 0) name = stem;
        var fw = new DigitFramesWindow(this, files, name);
        if (!fw.Ask() || fw.Frames.Count == 0) return;
        // 昼と夜は同じ大きさ・同じ並び（夜の画像のないコマは昼の絵）
        var (strip, nightStrip, interval) = fw.Build();
        name = PsdImport.SafeFileName(fw.FrameName.Length > 0 ? fw.FrameName : name);
        var test = new DigitTestWindow(this, name, strip, nightStrip, strip.Height, interval,
            $"コマ割を作ったときの 1 コマの高さです（{fw.Frames.Count} コマ）", DigitDefaultSubject,
            nightName: nightStrip != null ? $"{fw.Frames.Count(f => f.Night != null)} コマに夜の画像あり" : null);
        if (!test.Ask()) return;
        string dayRel;
        string? nightRel;
        try { (dayRel, nightRel) = WritePanelPng("digit", name, strip, nightStrip, pow2: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"画像を書き出せませんでした:\n{ex.Message}");
            return;
        }
        PlaceDigit(name, dayRel, nightRel, strip.Width, test.Interval, test.Subject, at ?? Canvas.ViewCenterPanel,
            $"{fw.Frames.Count} コマのコマ割を作って書き出しました（{dayRel}"
            + (DigitStrip.IsPanelSize(strip.Width, strip.Height) ? "" : $"。BVE のパネル用に {PngWriter.NextPow2(strip.Width)}×{PngWriter.NextPow2(strip.Height)} に広げています") + "）。");
    }

    // ================= 共通 =================

    /// <summary>
    /// パネルのフォルダの sub フォルダに「名前.png」（と夜は「名前_N.png」）を書き出し、パネルからの場所を返す。
    /// pow2 なら BVE のパネル用の大きさ（2 の累乗）に広げる（左上に詰めて透明で埋める）。すでにあるファイルは上書きせず、名前に _2 などを付ける。
    /// </summary>
    private (string Day, string? Night) WritePanelPng(string sub, string name, PsdPixels day, PsdPixels? night, bool pow2)
    {
        var dir = Path.Combine(PanelDir!, sub);
        var baseName = PsdImport.SafeFileName(name);
        var n = baseName;
        for (int i = 2; File.Exists(Path.Combine(dir, n + ".png")) || File.Exists(Path.Combine(dir, n + "_N.png")); i++) n = $"{baseName}_{i}";
        byte[] Enc(PsdPixels p) => pow2 ? PngWriter.EncodePow2(p) : PngWriter.Encode(p);
        var dp = Path.Combine(dir, n + ".png");
        WriteNewFile(dp, Enc(day));
        string? np = null;
        if (night != null)
        {
            np = Path.Combine(dir, n + "_N.png");
            WriteNewFile(np, Enc(night));
        }
        return (PsdImport.PathFrom(PanelDir!, dp), np != null ? PsdImport.PathFrom(PanelDir!, np) : null);
    }

    /// <summary>デジタル数字を置く（at を中心に、1 コマぶんの大きさで）。</summary>
    private void PlaceDigit(string title, string dayRel, string? nightRel, int width, int interval, string subject, Point at, string note)
    {
        var sec = PanelSection.Create(SectionKind.DigitalNumber);
        sec.SetTitle(title);
        sec.Set("Subject", subject.Length > 0 ? subject : DigitDefaultSubject);
        sec.Set("Location", PanelValues.Point(Math.Round(at.X - width / 2.0), Math.Round(at.Y - interval / 2.0)));
        sec.Set("DaytimeImage", dayRel);
        if (nightRel != null) sec.Set("NighttimeImage", nightRel);
        sec.Set("Layer", NextLayer().ToString());
        sec.Set("Interval", interval.ToString());
        AddImportedSections([sec], $"デジタル数字「{title}」を置きました（1 コマの高さ {interval}・状態量 {sec.Get("Subject")}）。{note}");
    }

    /// <summary>読み込んだオブジェクトをパネルに足して選ぶ（選んでいる間は位置ロック中でも動かせる）。</summary>
    private void AddImportedSections(List<PanelSection> sections, string message)
    {
        var added = new List<int>();
        var place = GetInsertPlace(); // 選んでいるオブジェクトのすぐ下（手前）に、同じレイヤーで
        foreach (var s in sections) added.Add(InsertNewSection(s, place, added.Count));
        CommitModel();
        _cache.Clear();
        RebuildAll();
        ApplySelection(added[0], added, null, SelectionSource.Other);
        Canvas.AllowFreshEdit(added);
        Canvas.Focus();
        Message(message + "状態量やレイヤーは右の欄で直せます。追加したオブジェクトは、選んでいる間だけ位置ロック中でも動かせます");
    }
}
