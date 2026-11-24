using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BvePanelEditor.Core;
using BvePanelEditor.Core.Imaging;
using BvePanelEditor.Core.Psd;
using BvePanelEditor.Rendering;
using BvePanelEditor.Views;

namespace BvePanelEditor;

/// <summary>
/// 画像を 4 隅で変形して貼りこむ（正面から描いたモニタ・スタフなどを、パネルの斜めの面に合わせて置く）。
/// 変形の方法（元の画像・4 隅・1 コマの高さ）はオブジェクトに覚え（「;@変形:」の行）、
/// あとから元の画像を変えても、4 隅を直しても、同じように変形し直せる。
/// </summary>
public partial class MainWindow
{
    /// <summary>確認用: 変形の画面の代わりに 4 隅を決める（false ならやめる）。</summary>
    public static Func<WarpPlaceWindow, bool>? WarpAnswer;
    /// <summary>確認用: 種類（0 = 常点灯、1 = DigitalNumber）と、1 コマの高さの答え。</summary>
    public static Func<int>? WarpKindAnswer;
    public static Func<int, string?>? WarpIntervalAnswer;
    /// <summary>確認用: 「画像を変える」で選ぶファイル（null ならやめる）。</summary>
    public static Func<string?>? WarpPickAnswer;

    /// <summary>変形の元の画像を読んだもの。</summary>
    private sealed record WarpSource(PsdPixels Day, PsdPixels? Night, int Interval, double? SvgScale, string? NightFile);

    private static string WarpFileFilter => "画像・SVG|" + string.Join(";", ImageExtensions.Append(".svg").Select(x => "*" + x)) + "|すべて|*.*";

    /// <summary>
    /// オブジェクトを追加 → 画像を 4 隅で変形して貼りこむ: 正面から描いた画像（モニタ・スタフなど）を選び、
    /// 常点灯画像か DigitalNumber（コマ割済、1 コマずつ同じ 4 隅で変形）として、パネルの斜めの面に合わせて置く。
    /// </summary>
    private void OnWarpImport(object? sender, RoutedEventArgs? e)
    {
        if (!EnsureSavedPanelForImages()) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { AddToRecent = false, Title = "変形して貼りこむ画像", Filter = WarpFileFilter };
        if (PanelDir != null) dlg.InitialDirectory = PanelDir;
        if (dlg.ShowDialog(this) != true) return;
        WarpImport(dlg.FileName, null, null);
    }

    /// <summary>
    /// 画像 1 枚を、4 隅で変形して貼りこむ。asKind は 0 = 常点灯、1 = DigitalNumber（null なら聞く）。
    /// at は最初に置く場所の中心（null ならパネル画面の真ん中）。やめた・できなかったら false。
    /// </summary>
    public bool WarpImport(string file, int? asKind, Point? at)
    {
        if (_doc == null) return false;
        if (PanelDir == null) { Warn("先にパネルファイルを保存してください（画像をパネルからの場所で指すため）。"); return false; }
        int kind = asKind ?? (WarpKindAnswer != null ? WarpKindAnswer() : ChoiceDialog.Show(this, "画像を変形して貼りこみます", $"「{Path.GetFileName(file)}」をどう貼りこみますか？",
        [
            "常点灯画像として貼りこむ（表示灯・Subject = true）",
            "DigitalNumber（コマ割済）として貼りこむ … 1 コマずつ同じ形に変形します",
        ]));
        if (kind < 0) return false;
        file = Path.GetFullPath(file);
        if (LoadWarpSource(file, null, kind == 1, svgScale: null, scaleInitial: 1, intervalInitial: null) is not { } src) return false;

        var w = new WarpPlaceWindow(this, $"4 隅で変形して貼りこむ: {Path.GetFileName(file)}", PanelBitmapForWarp(), src.Day, src.Interval, at ?? Canvas.ViewCenterPanel);
        if (WarpAnswer != null) { if (!WarpAnswer(w)) return false; }
        else if (w.ShowDialog() != true) return false;

        var name = Path.GetFileNameWithoutExtension(file);
        var sec = PanelSection.Create(kind == 1 ? SectionKind.DigitalNumber : SectionKind.PilotLamp);
        sec.SetTitle(name);
        sec.Set("Subject", kind == 1 ? DigitDefaultSubject : "true");
        var info = new WarpInfo
        {
            Source = PathFromPanel(file), Night = src.NightFile != null ? PathFromPanel(src.NightFile) : null,
            Corners = w.Corners.Select(p => (p.X, p.Y)).ToArray(), Interval = src.Interval, SvgScale = src.SvgScale,
        };
        if (ApplyWarp(sec, info, src) is not { } rel) return false;
        sec.Set("Layer", NextLayer().ToString());
        AddImportedSections([sec], kind == 1
            ? $"デジタル数字「{name}」を 4 隅で変形して置きました（{rel}、1 コマの高さ {sec.Get("Interval")}・状態量 {DigitDefaultSubject}）。"
            : $"常点灯画像「{name}」を 4 隅で変形して置きました（{rel}）。");
        return true;
    }

    /// <summary>右の欄の「画像を変える…」: 変形して貼りこんだオブジェクトの元の画像を変え、覚えている 4 隅で変形し直す。</summary>
    private void OnWarpChangeImage()
    {
        if (WarpTarget() is not { } sec || sec.Warp is not { } old) return;
        string? file;
        if (WarpPickAnswer != null) file = WarpPickAnswer();
        else
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { AddToRecent = false, Title = "変形して貼りこむ画像（今の 4 隅で変形し直します）", Filter = WarpFileFilter };
            var cur = FullFromPanel(old.Source);
            if (Path.GetDirectoryName(cur) is { } d && Directory.Exists(d)) dlg.InitialDirectory = d;
            file = dlg.ShowDialog(this) == true ? dlg.FileName : null;
        }
        if (file == null) return;
        file = Path.GetFullPath(file);
        bool digit = sec.Kind == SectionKind.DigitalNumber;
        if (LoadWarpSource(file, null, digit, svgScale: null, scaleInitial: old.SvgScale ?? 1, intervalInitial: old.Interval > 0 ? old.Interval : null) is not { } src) return;
        var info = new WarpInfo
        {
            Source = PathFromPanel(file), Night = src.NightFile != null ? PathFromPanel(src.NightFile) : null,
            Corners = old.Corners, Interval = src.Interval, SvgScale = src.SvgScale,
        };
        RewarpSection(sec, info, src, $"元の画像を「{Path.GetFileName(file)}」に変えて、同じ 4 隅で変形し直しました");
    }

    /// <summary>右の欄の「4 隅を直す…」: 覚えている元の画像で、4 隅を決め直して変形し直す。</summary>
    private void OnWarpEditCorners()
    {
        if (WarpTarget() is not { } sec || sec.Warp is not { } old) return;
        var file = FullFromPanel(old.Source);
        if (!File.Exists(file)) { Warn($"変形の元の画像が見つかりません:\n{old.Source}\n「画像を変える…」で選び直してください。"); return; }
        var night = old.Night != null ? FullFromPanel(old.Night) : null;
        if (LoadWarpSource(file, night, sec.Kind == SectionKind.DigitalNumber, svgScale: old.SvgScale ?? 1, scaleInitial: 1, intervalInitial: old.Interval, askInterval: false) is not { } src) return;
        var corners = old.Corners.Select(c => new Point(c.X, c.Y)).ToArray();
        var w = new WarpPlaceWindow(this, $"4 隅を直す: {Path.GetFileName(file)}", PanelBitmapForWarp(sec), src.Day, src.Interval, Canvas.ViewCenterPanel, corners);
        if (WarpAnswer != null) { if (!WarpAnswer(w)) return; }
        else if (w.ShowDialog() != true) return;
        var info = old.Clone();
        info.Corners = w.Corners.Select(p => (p.X, p.Y)).ToArray();
        RewarpSection(sec, info, src, "4 隅を直して変形し直しました");
    }

    /// <summary>右の欄の操作の対象（変形して貼りこんだオブジェクトを 1 つ選んでいるとき）。</summary>
    private PanelSection? WarpTarget()
    {
        if (_doc == null || _selSet.Count != 1 || _selected < 0 || _selected >= _doc.Sections.Count) return null;
        if (PanelDir == null) { Warn("先にパネルファイルを保存してください（画像をパネルからの場所で指すため）。"); return null; }
        FlushReparse();
        return _doc.Sections[_selected].Warp != null ? _doc.Sections[_selected] : null;
    }

    private void RewarpSection(PanelSection sec, WarpInfo info, WarpSource src, string message)
    {
        if (ApplyWarp(sec, info, src) is not { } rel) return;
        CommitModel();
        _cache.Clear();
        RebuildAll();
        Message($"{message}（{rel}）。");
    }

    /// <summary>
    /// 変形した画像を書き出し、オブジェクトの画像・位置（DigitalNumber は 1 コマの高さも）・変形の方法を書き換える。
    /// 画像は「元の名前_warp.png」（夜は _warp_N.png）で warp フォルダに書く（同じ名前があれば _2 などを付ける）。書き出した昼の画像の場所を返す。
    /// </summary>
    private string? ApplyWarp(PanelSection sec, WarpInfo info, WarpSource src)
    {
        var q = info.Corners;
        var baseName = Path.GetFileNameWithoutExtension(info.Source) + "_warp";
        try
        {
            PsdPixels day; PsdPixels? night;
            int interval = 0;
            if (sec.Kind == SectionKind.DigitalNumber)
            {
                (day, interval) = Warp.FramesToQuad(src.Day, src.Interval, q[0], q[1], q[2], q[3]);
                night = src.Night != null ? Warp.FramesToQuad(src.Night, src.Interval, q[0], q[1], q[2], q[3]).Strip : null;
            }
            else
            {
                day = Warp.ToQuad(src.Day, q[0], q[1], q[2], q[3]);
                night = src.Night != null ? Warp.ToQuad(src.Night, q[0], q[1], q[2], q[3]) : null;
            }
            var (dayRel, nightRel) = WritePanelPng("warp", baseName, day, night, pow2: true);
            sec.Set("Location", PanelValues.Point(day.Bounds.Left, day.Bounds.Top));
            sec.Set("DaytimeImage", dayRel);
            sec.Set("NighttimeImage", nightRel);
            if (interval > 0) sec.Set("Interval", interval.ToString());
            sec.Warp = info;
            return dayRel;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"画像を書き出せませんでした:\n{ex.Message}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
        {
            Warn(ex.Message);
        }
        return null;
    }

    /// <summary>
    /// 変形の元の画像を読む。SVG は倍率（svgScale、null なら聞く）で描く。夜の画像は nightFile、なければ名前の決まり（～_N など）で探す。
    /// DigitalNumber なら 1 コマの高さを求めて聞き（askInterval）、2 の累乗に広げた下の余りは使わない。やめた・読めなかったら null。
    /// </summary>
    private WarpSource? LoadWarpSource(string file, string? nightFile, bool digit, double? svgScale, double scaleInitial, int? intervalInitial, bool askInterval = true)
    {
        PsdPixels? day, night = null;
        double? usedScale = null;
        if (SvgImage.IsSvg(file))
        {
            try
            {
                var svg = SvgImage.Load(file);
                usedScale = svgScale ?? AskSvgScale(file, svg, 1, scaleInitial);
                if (usedScale == null) return null;
                day = SvgImage.Rasterize(svg, usedScale.Value);
                if (nightFile != null && File.Exists(nightFile)) night = SvgImage.Rasterize(SvgImage.Load(nightFile), usedScale.Value);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Warn($"「{Path.GetFileName(file)}」を読めませんでした。\n{ex.Message}");
                return null;
            }
        }
        else
        {
            day = ImageLoader.TryLoadPixels(file);
            nightFile ??= NeedlePickerWindow.FindNight(file);
            if (nightFile != null) night = ImageLoader.TryLoadPixels(nightFile);
        }
        if (day == null || day.Width == 0 || day.Height == 0) { Warn($"「{Path.GetFileName(file)}」を読めませんでした。"); return null; }
        if (night != null && (night.Width != day.Width || night.Height != day.Height)) { night = null; nightFile = null; }
        if (night == null) nightFile = null;

        int interval = 0;
        if (digit)
        {
            int? content = !SvgImage.IsSvg(file) && PngWriter.TryReadContentSize(file, out _, out var ch) ? ch : null;
            if (askInterval)
            {
                var found = DigitalFrames.Detect(day, content);
                int guess = found is { Confident: true } ? found.Interval : intervalInitial is { } ii && ii <= day.Height ? ii : found?.Interval ?? (day.Height >= 10 ? day.Height / 10 : day.Height);
                string msg = $"1 コマの高さを入力してください（画像の高さ {day.Height}）。" + (found is { Confident: true } ? "" : "\n入っている値は推定です。");
                string? ans = WarpIntervalAnswer != null ? WarpIntervalAnswer(guess) : InputDialog.Show(this, "1 コマの高さ", msg, guess.ToString());
                if (ans == null) return null;
                if (!int.TryParse(ans.Trim(), out interval) || interval <= 0 || interval > day.Height) { Warn($"1 コマの高さは 1〜{day.Height} の数で入力してください。"); return null; }
            }
            else interval = intervalInitial is { } ii && ii > 0 && ii <= day.Height ? ii : day.Height;
            if (content is { } c && c < day.Height) day = Warp.Crop(day, 0, 0, day.Width, c);
            if (night != null && content is { } cn && cn < night.Height) night = Warp.Crop(night, 0, 0, night.Width, cn);
        }
        return new WarpSource(day, night, interval, usedScale, nightFile);
    }

    /// <summary>パネルファイルから見た場所（別のドライブならフルパス）。</summary>
    private string PathFromPanel(string full) => Path.GetRelativePath(PanelDir!, full);

    private string FullFromPanel(string rel) => Path.GetFullPath(Path.IsPathRooted(rel) ? rel : Path.Combine(PanelDir!, rel));

    /// <summary>
    /// 変形の画面に敷くパネルの絵（昼）。パネル本体の画像と、表示灯・デジタル数字（最初のコマ）を、パネルくんで見えているとおりに重ねる。
    /// except は描かない（変形し直すオブジェクトそのもの）。
    /// </summary>
    private BitmapSource? PanelBitmapForWarp(PanelSection? except = null)
    {
        if (_scene == null || _doc == null) return null;
        int skip = except != null ? _doc.Sections.IndexOf(except) : -1;
        var size = _scene.ContentSize;
        int w = Math.Max(1, (int)Math.Ceiling(size.Width)), h = Math.Max(1, (int)Math.Ceiling(size.Height));
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            if (!_scene.ThisHidden && _scene.BaseDay is { } b) dc.DrawImage(b, new Rect(0, 0, b.PixelWidth, b.PixelHeight));
            foreach (var it in _scene.Items.Where(i => !i.Hidden && i.Day != null && i.Section != skip).OrderBy(i => i.Layer).ThenBy(i => i.Section))
            {
                var img = it.Day!;
                if (it.Kind == SectionKind.PilotLamp)
                    dc.DrawImage(img, new Rect(it.Location.X, it.Location.Y, img.PixelWidth, img.PixelHeight));
                else if (it.Kind == SectionKind.DigitalNumber && it.Interval > 0 && it.Interval <= img.PixelHeight)
                    dc.DrawImage(new CroppedBitmap(img, new Int32Rect(0, 0, img.PixelWidth, it.Interval)), new Rect(it.Location.X, it.Location.Y, img.PixelWidth, it.Interval));
            }
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }
}
