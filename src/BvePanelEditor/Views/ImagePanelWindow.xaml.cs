using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BvePanelEditor.Core;
using BvePanelEditor.Core.Crop;
using BvePanelEditor.Core.Psd;
using BvePanelEditor.Rendering;
using Microsoft.Win32;

namespace BvePanelEditor.Views;

/// <summary>囲みの一覧の 1 行。</summary>
public sealed class RegionRow
{
    public required CropRegion Region { get; init; }
    public string Text { get; init; } = "";
}

/// <summary>下に敷く・上に重ねる画像 1 つ（見本用に読み込んだ絵も持つ）。</summary>
public sealed class OverlayItem
{
    public required CropOverlay Overlay;
    public PsdPixels? DayPx, NightPx;
    /// <summary>部品用（切り抜いて利用。画像全体は重ねない。名前が切り抜き元の組の名前になる）。「重ねる画像」のタブの下の一覧に出す。</summary>
    public bool CropSource;
    /// <summary>切り抜く元のとき、この画像だけを切り抜く（透明な所は透明のまま）。false なら透明な所へメインパネルを重ねてから切り抜く。</summary>
    public bool LayerOnly;
}

/// <summary>4 点の画像（昼・夜 × 無発光・発光）から表示灯を切り抜いて、パネルを作る画面。</summary>
public partial class ImagePanelWindow : Window
{
    public static readonly string[] SlotLabels = ["昼・無発光", "昼・発光", "夜・無発光", "夜・発光"];
    private const int DayUnlit = 0, DayLit = 1, NightUnlit = 2, NightLit = 3;

    private readonly string _panelDir;
    private readonly int _layer;
    private readonly string?[] _paths = new string?[4];
    private readonly PsdPixels?[] _px = new PsdPixels?[4];
    private readonly BitmapSource?[] _bmp = new BitmapSource?[4];
    /// <summary>PSD のレイヤーを重ねた欄なら、そのレイヤー（_paths はその PSD）。画像ファイルなら null。</summary>
    private readonly List<string>?[] _layers = new List<string>?[4];
    private readonly List<OverlayItem> _overlays = [];
    private readonly List<CropRegion> _regions = [];
    private bool _suppress;
    private bool _fitted;

    /// <summary>［パネルを作る］で決まった指定。</summary>
    public CropBuildSpec? Spec { get; private set; }

    public ImagePanelWindow(Window owner, string panelDir, string? firstImage, int layer)
    {
        InitializeComponent();
        Owner = owner;
        _panelDir = panelDir;
        _layer = Math.Max(1, layer);
        OutBox.Text = Path.Combine(panelDir, "crop");
        SubjectBox.ItemsSource = Subjects.Suggestions().ToList();
        Canvas.Regions = _regions;
        Canvas.NextName = NextName;
        Canvas.SelectionChanged += () => { SyncListSelection(); ShowProps(); };
        Canvas.RegionsChanged += () => { foreach (var r in _regions.Where(r => r.Layer < 1)) r.Layer = _layer; RefreshList(); ShowProps(); };
        Canvas.DeleteRequested += () => OnDeleteRegions(this, new RoutedEventArgs());
        Canvas.PanelMouseMove += p => CoordText.Text = $"X {p.X:0}  Y {p.Y:0}";
        Canvas.OverlayDragStarted += OnOverlayDragStarted;
        Canvas.OverlayDragging += OnOverlayDragging;
        Canvas.OverlayDragFinished += OnOverlayDragFinished;
        Canvas.OverlayNudged += MoveSelectedOverlay;
        Canvas.OverlayPicked += OnOverlayPicked;
        RefreshOverlays();
        Loaded += (_, _) =>
        {
            if (firstImage != null) LoadWithGuess(DayUnlit, firstImage);
            UpdateSummary();
        };
    }

    // ================= 画像 =================

    private void OnBrowseImage(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string t } || !int.TryParse(t, out var slot)) return;
        var dlg = new OpenFileDialog { AddToRecent = false, Title = $"{SlotLabels[slot]}の画像を選ぶ", Filter = "画像・PSD|*.png;*.bmp;*.jpg;*.jpeg;*.gif;*.tif;*.tiff;*.psd|すべて|*.*" };
        var known = _paths.FirstOrDefault(p => p != null);
        if (known != null) dlg.InitialDirectory = Path.GetDirectoryName(known);
        if (dlg.ShowDialog(this) == true) LoadWithGuess(slot, dlg.FileName);
    }

    private static bool IsPsd(string path) => Path.GetExtension(path).Equals(".psd", StringComparison.OrdinalIgnoreCase);

    /// <summary>1 点を読み、まだ空いている欄は名前から探して読む。PSD ならレイヤーを振り分ける画面を開く。</summary>
    public void LoadWithGuess(int slot, string path)
    {
        if (IsPsd(path)) { AssignPsd(slot, path); return; }
        bool first = _paths.All(p => p == null);
        if (!LoadSlot(slot, path)) return;
        var guess = Guess(path);
        for (int i = 0; i < 4; i++)
            if (_paths[i] == null && guess[i] != null) LoadSlot(i, guess[i]!);
        if (first) ViewCombo.SelectedItem = null; // 最初は「差（夜）」で表示する（光る所が分かりやすい）
        RefreshViews();
    }

    private bool LoadSlot(int slot, string path)
    {
        try
        {
            var (px, bmp) = ImageLoader.Load(path);
            var refPx = _px.Where((p, i) => p != null && i != slot).FirstOrDefault();
            if (refPx != null && (refPx.Width != px.Width || refPx.Height != px.Height))
                MessageBox.Show(this, $"{SlotLabels[slot]}の画像の大きさ（{px.Width}×{px.Height}）が、ほかの画像（{refPx.Width}×{refPx.Height}）と違います。\n位置がずれるので、同じ大きさの画像を使ってください。",
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            SetSlot(slot, path, null, px, bmp);
            return true;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or InvalidDataException)
        {
            MessageBox.Show(this, $"画像を読めませんでした:\n{e.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private TextBox SlotBox(int slot) => (TextBox)FindName(new[] { "DayUnlitBox", "DayLitBox", "NightUnlitBox", "NightLitBox" }[slot]);

    private void SetSlot(int slot, string path, List<string>? layers, PsdPixels px, BitmapSource bmp)
    {
        _paths[slot] = path;
        _layers[slot] = layers;
        _px[slot] = px;
        _bmp[slot] = bmp;
        SlotBox(slot).Text = layers != null ? $"{PathDisplay.Show(path)}（レイヤー {layers.Count} 個を重ねる）" : PathDisplay.Show(path);
        RefreshViews();
    }

    public static readonly CropSlotKind[] SlotKinds = [new(false, false), new(false, true), new(true, false), new(true, true)];

    /// <summary>
    /// PSD を slot の欄に選んだ。同じ名前の付け方の PSD（夜の PSD など）も探して、レイヤーを振り分ける画面を開く。
    /// すでに画像を選んである欄は、チェックしない限りそのまま。
    /// </summary>
    public void AssignPsd(int slot, string path)
    {
        var (paths, colPsd, colLayers) = AssignInput(slot, path);
        var dlg = new LayerAssignWindow(this, paths, SlotLabels, SlotKinds, colPsd, colLayers);
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        ApplyPicks(dlg.Result);
    }

    /// <summary>振り分ける画面に渡すもの。path が null なら、今 PSD を使っている欄だけで開く。</summary>
    public (List<string> Paths, string?[] ColPsd, List<string>?[] ColLayers) AssignInput(int slot, string? path)
    {
        var paths = new List<string>();
        void Add(string? p)
        {
            if (p != null && IsPsd(p) && File.Exists(p) && !paths.Any(x => string.Equals(Path.GetFullPath(x), Path.GetFullPath(p), StringComparison.OrdinalIgnoreCase)))
                paths.Add(p);
        }
        var colPsd = new string?[4];
        var colLayers = new List<string>?[4];
        for (int i = 0; i < 4; i++)
            if (_layers[i] != null && _paths[i] != null) { colPsd[i] = _paths[i]; colLayers[i] = _layers[i]; }
        if (path != null)
        {
            Add(path);
            var guess = Guess(path);
            colPsd[slot] = path;
            colLayers[slot] = null;
            for (int i = 0; i < 4; i++)
                if (i != slot && _paths[i] == null && guess[i] != null && IsPsd(guess[i]!)) colPsd[i] = guess[i];
            // 空いている欄は、同じ時間帯（昼／夜）のもう一方と同じ PSD、それもなければ選んだ PSD
            // （名前から振り分けて、夜らしいレイヤーがなければ夜の欄は空のまま）
            for (int i = 0; i < 4; i++)
            {
                if (colPsd[i] != null || _paths[i] != null) continue;
                colPsd[i] = colPsd[i ^ 1] ?? path;
            }
        }
        foreach (var p in colPsd) Add(p);
        foreach (var p in _paths) Add(p);
        return (paths, colPsd, colLayers);
    }

    private void ApplyPicks(LayerPick?[] picks)
    {
        bool first = _paths.All(p => p == null);
        for (int k = 0; k < 4; k++)
        {
            if (picks[k] is not { } pick) continue;
            var refPx = _px.Where((p, i) => p != null && i != k && picks[i] == null).FirstOrDefault()
                ?? picks.FirstOrDefault(p => p != null && p != pick)?.Pixels;
            if (refPx != null && (refPx.Width != pick.Pixels.Width || refPx.Height != pick.Pixels.Height))
                MessageBox.Show(this, $"{SlotLabels[k]}の PSD の大きさ（{pick.Pixels.Width}×{pick.Pixels.Height}）が、ほかの画像（{refPx.Width}×{refPx.Height}）と違います。\n位置がずれるので、同じ大きさの画像を使ってください。",
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            SetSlot(k, pick.PsdPath, pick.Layers, pick.Pixels, ImageLoader.ToBitmap(pick.Pixels));
        }
        if (first) ViewCombo.SelectedItem = null;
        RefreshViews();
        UpdateSummary();
    }

    private void OnAssignLayers(object sender, RoutedEventArgs e)
    {
        if (!_paths.Any(p => p != null && IsPsd(p)))
        {
            var dlg = new OpenFileDialog { AddToRecent = false, Title = "レイヤーを振り分ける PSD を選ぶ", Filter = "Photoshop|*.psd" };
            var known = _paths.FirstOrDefault(p => p != null);
            if (known != null) dlg.InitialDirectory = Path.GetDirectoryName(known);
            if (dlg.ShowDialog(this) != true) return;
            int free = Array.FindIndex(_paths, p => p == null);
            AssignPsd(free >= 0 ? free : DayUnlit, dlg.FileName);
            return;
        }
        var (paths, colPsd, colLayers) = AssignInput(-1, null);
        var w = new LayerAssignWindow(this, paths, SlotLabels, SlotKinds, colPsd, colLayers);
        if (w.ShowDialog() == true && w.Result != null) ApplyPicks(w.Result);
    }

    /// <summary>
    /// 1 点のファイル名から 4 点を探す。例: BVEPanel.png → BVEPanel_TT / BVEPanel_N / BVEPanel_N_TT。
    /// 末尾の「_N」「N」「_TT」などを外した名前を土台にして、いちばん多く見つかった土台を使う。
    /// </summary>
    public static string?[] Guess(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        var ext = Path.GetExtension(path);
        var name = Path.GetFileNameWithoutExtension(path);
        var stems = new List<string> { name };
        foreach (var pat in new[] { @"(?i)_?N_?TT$", @"(?i)_?TT_?N$", @"(?i)_?TT$", @"(?i)_N$", @"(?i)N$", @"_?発光$", @"_?夜$", @"_?夜_?発光$" })
        {
            var s = Regex.Replace(name, pat, "");
            if (s.Length > 0 && !stems.Contains(s)) stems.Add(s);
        }
        string[][] patterns =
        [
            ["{0}", "{0}_昼"],
            ["{0}_TT", "{0}TT", "{0}_tt", "{0}_発光", "{0}_on"],
            ["{0}_N", "{0}N", "{0}_n", "{0}_夜", "{0}_night"],
            ["{0}_N_TT", "{0}N_TT", "{0}_TT_N", "{0}_n_tt", "{0}_夜_発光", "{0}_夜発光", "{0}NTT"],
        ];
        string?[] best = new string?[4];
        int bestCount = -1;
        foreach (var stem in stems)
        {
            var found = new string?[4];
            for (int slot = 0; slot < 4; slot++)
                foreach (var p in patterns[slot])
                {
                    var f = Path.Combine(dir, string.Format(p, stem) + ext);
                    if (File.Exists(f)) { found[slot] = f; break; }
                }
            int count = found.Count(f => f != null);
            if (count > bestCount) { bestCount = count; best = found; }
        }
        return best;
    }

    private void OnBrowseOut(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "PNG を書き出すフォルダ", InitialDirectory = Directory.Exists(OutBox.Text) ? OutBox.Text : _panelDir };
        if (dlg.ShowDialog(this) == true) OutBox.Text = dlg.FolderName;
    }

    /// <summary>部品を切り抜く元にした重ねる画像（扉違いなど）。</summary>
    private IEnumerable<OverlayItem> CropSources => _overlays.Where(x => x.CropSource);

    /// <summary>
    /// 組の名前から、昼・夜の切り抜き元の画像（「発光」か、部品を切り抜く元にした重ねる画像）。
    /// 切り抜く元は、切り抜く絵（そのレイヤーのみなら画像のまま、でなければ透明な所へメインパネルを重ねた絵）。
    /// forDetect なら、部品を探す用にいつもメインパネルに重ねた絵（透明な所が差に出ないように）。
    /// </summary>
    private (PsdPixels? Day, PsdPixels? Night) SetImages(string name, bool forDetect = false)
    {
        if (name == CropSetNames.Lit) return (_px[DayLit], _px[NightLit]);
        var x = CropSources.FirstOrDefault(s => s.Overlay.Name == name);
        if (x == null) return (null, null);
        return x.LayerOnly && !forDetect ? (x.DayPx, x.NightPx) : (OverMain(x.DayPx, false), OverMain(x.NightPx, true));
    }

    private readonly Dictionary<(PsdPixels, PsdPixels), PsdPixels> _overMainCache = [];

    /// <summary>透明な所へメインパネル（無発光）を重ねた絵。透明な所がなければそのまま。</summary>
    private PsdPixels? OverMain(PsdPixels? img, bool night)
    {
        var under = (night ? _px[NightUnlit] : null) ?? _px[DayUnlit];
        if (img == null || under == null) return img;
        if (_overMainCache.TryGetValue((img, under), out var done)) return done;
        var r = CropCompose.HasTransparency(img) ? CropCompose.Over(under, img) : img;
        _overMainCache[(img, under)] = r;
        return r;
    }

    /// <summary>「部品を探す」の基準: どの切り抜き元の、昼か夜の差から探すか。</summary>
    private sealed record BasisItem(string Label, string Set, bool Night)
    {
        public override string ToString() => Label;
    }

    /// <summary>囲みの「切り抜き元」と「部品を探す」の基準の選択肢を、切り抜き元に合わせて作り直す。</summary>
    private void RefreshCropSources()
    {
        SetBox.ItemsSource = new[] { CropSetNames.Lit }.Concat(CropSources.Select(x => x.Overlay.Name)).ToList();
        var current = (BasisCombo.SelectedItem as BasisItem)?.Label;
        var items = new List<BasisItem>
        {
            new("夜（夜・発光 − 夜・無発光）", CropSetNames.Lit, true),
            new("昼（昼・発光 − 昼・無発光）", CropSetNames.Lit, false),
        };
        foreach (var x in CropSources)
        {
            var n = x.Overlay.Name;
            if (x.NightPx != null) items.Add(new($"{n}（{n}・夜 − 夜・無発光）", n, true));
            items.Add(new($"{n}（{n}・昼 − 昼・無発光）", n, false));
        }
        BasisCombo.ItemsSource = items;
        BasisCombo.SelectedItem = items.FirstOrDefault(i => i.Label == current) ?? items[0];
    }

    // ================= 表示 =================

    private sealed record ViewItem(string Label, Func<BitmapSource?> Image)
    {
        public override string ToString() => Label;
    }

    private void RefreshViews()
    {
        var current = (ViewCombo.SelectedItem as ViewItem)?.Label;
        var items = new List<ViewItem>();
        for (int i = 0; i < 4; i++) { int k = i; if (_bmp[k] != null) items.Add(new(SlotLabels[k], () => _bmp[k])); }
        if (_px[NightUnlit] != null && _px[NightLit] != null) items.Add(new("差（夜）: 光る所を強調", () => DiffBitmap(_px[NightUnlit]!, _px[NightLit]!)));
        if (_px[DayUnlit] != null && _px[DayLit] != null) items.Add(new("差（昼）: 光る所を強調", () => DiffBitmap(_px[DayUnlit]!, _px[DayLit]!)));
        foreach (var x in CropSources)
        {
            var s = x;
            if (s.DayPx != null) items.Add(new($"{s.Overlay.Name}（昼）: 部品用", () => ImageLoader.ToBitmap(SetImages(s.Overlay.Name).Day ?? s.DayPx)));
            if (s.NightPx != null) items.Add(new($"{s.Overlay.Name}（夜）: 部品用", () => ImageLoader.ToBitmap(SetImages(s.Overlay.Name).Night ?? s.NightPx)));
        }
        if (_px[DayUnlit] != null)
        {
            items.Add(new("重ねた見本（昼）: 下に敷く画像＋メインパネル＋上に重ねる画像", () => ImageLoader.ToBitmap(Stacked(night: false))));
            if (_px[NightUnlit] != null) items.Add(new("重ねた見本（夜）: 下に敷く画像＋メインパネル＋上に重ねる画像", () => ImageLoader.ToBitmap(Stacked(night: true))));
        }
        _suppress = true;
        ViewCombo.ItemsSource = items;
        ViewCombo.SelectedItem = items.FirstOrDefault(i => i.Label == current)
            ?? items.FirstOrDefault(i => i.Label.StartsWith("差（夜）")) ?? items.FirstOrDefault(i => i.Label == SlotLabels[DayLit]) ?? items.FirstOrDefault();
        _suppress = false;
        ShowView();
    }

    private void OnViewChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppress) { ShowView(); UpdateOverlayFrame(); }
    }

    private void ShowView()
    {
        Canvas.Image = (ViewCombo.SelectedItem as ViewItem)?.Image();
        if (!_fitted && Canvas.Image != null)
        {
            _fitted = true;
            Dispatcher.BeginInvoke(() => Canvas.FitAll(), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void OnFit(object sender, RoutedEventArgs e) => Canvas.FitAll();

    /// <summary>差の見本: 無発光を暗くして、変わった所を発光の色のまま明るく出す。</summary>
    private BitmapSource DiffBitmap(PsdPixels unlit, PsdPixels lit)
    {
        int threshold = (int)ThresholdSlider.Value;
        int w = Math.Min(unlit.Width, lit.Width), h = Math.Min(unlit.Height, lit.Height);
        var mask = RegionDetect.DiffMask(unlit, lit, new PsdRect(0, 0, w, h), threshold);
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4, u = (y * unlit.Width + x) * 4, l = (y * lit.Width + x) * 4;
                if (mask[y * w + x]) { px[o] = lit.Bgra[l]; px[o + 1] = lit.Bgra[l + 1]; px[o + 2] = lit.Bgra[l + 2]; }
                else { px[o] = (byte)(unlit.Bgra[u] / 4); px[o + 1] = (byte)(unlit.Bgra[u + 1] / 4); px[o + 2] = (byte)(unlit.Bgra[u + 2] / 4); }
                px[o + 3] = 255;
            }
        return ImageLoader.ToBitmap(new PsdPixels(new PsdRect(0, 0, w, h), px));
    }

    // ================= 自動で探す・分ける・まとめる =================

    /// <summary>今の基準の、比べる相手（ふつうは無発光）と切り抜き元（発光・扉など）の組と、その切り抜き元の名前。</summary>
    private (PsdPixels Unlit, PsdPixels Lit, string Set)? BasisPair()
    {
        var b = BasisCombo.SelectedItem as BasisItem ?? new BasisItem("", CropSetNames.Lit, true);
        return PairFor(b.Set, b.Night) is { } p ? (p.Unlit, p.Lit, b.Set) : null;
    }

    /// <summary>単純な変化の差で探す（基準が部品用の画像のときだけ）。</summary>
    private bool SimpleDiff(string set) => set != CropSetNames.Lit && SimpleDiffCheck.IsChecked == true;

    private void OnBasisChanged(object sender, SelectionChangedEventArgs e) =>
        SimpleDiffCheck.IsEnabled = BasisCombo.SelectedItem is BasisItem b && b.Set != CropSetNames.Lit;

    /// <summary>
    /// 切り抜き元 set の、昼か夜の無発光との組。なければ、もう片方で。
    /// 単純な変化の差で探すときは、部品用の画像を、同じ昼・夜の無発光・発光の近い方と組にする（表示灯の差を拾わないように）。
    /// </summary>
    private (PsdPixels Unlit, PsdPixels Lit)? PairFor(string set, bool night)
    {
        var (sd, sn) = SetImages(set, forDetect: true);
        bool n = night;
        var u = n ? _px[NightUnlit] : _px[DayUnlit];
        var l = n ? sn : sd;
        if (u == null || l == null)
        {
            n = !night;
            u = n ? _px[NightUnlit] : _px[DayUnlit];
            l = n ? sn : sd;
        }
        if (u == null || l == null) return null;
        if (SimpleDiff(set) && NearerIsLit(l, u, n ? _px[NightLit] : _px[DayLit])) u = (n ? _px[NightLit] : _px[DayLit])!;
        return (u, l);
    }

    /// <summary>部品用の画像が発光の方に近いか（同じ組み合わせは覚えておく）。</summary>
    private bool NearerIsLit(PsdPixels part, PsdPixels unlit, PsdPixels? lit)
    {
        if (lit == null) return false;
        if (_nearerCache.TryGetValue((part, unlit, lit), out var v)) return v;
        return _nearerCache[(part, unlit, lit)] = RegionDetect.NearerIsLit(part, unlit, lit, 8);
    }
    private readonly Dictionary<(PsdPixels, PsdPixels, PsdPixels), bool> _nearerCache = [];

    private string NextName()
    {
        int n = 1;
        var used = _regions.Select(r => r.Name).ToHashSet();
        while (used.Contains($"Lamp_{n:00}")) n++;
        return $"Lamp_{n:00}";
    }

    /// <summary>自動で探す。replace なら今の囲みを置き換え、そうでなければ重ならないものだけ足す。戻り値は足した数。</summary>
    public int Detect(bool replace)
    {
        if (BasisPair() is not { } pair)
        {
            MessageBox.Show(this, "無発光の画像と、基準の画像（発光・切り抜く元）を、昼か夜のどちらか 1 組そろえてください。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }
        int asked = (int)ThresholdSlider.Value;
        List<PsdRect> rects;
        if (SimpleDiff(pair.Set))
        {
            // 単純な変化の差: 近い方と比べて、変わった所をそのまま囲む（感度は変えず、大きな囲みも外さない）
            rects = RegionDetect.Detect(pair.Unlit, pair.Lit, asked, (int)GapSlider.Value);
            bool lit = pair.Unlit == _px[NightLit] || pair.Unlit == _px[DayLit];
            _detectNote = $"単純な変化の差: 「{pair.Set}」を{(lit ? "発光" : "無発光")}の画像と比べました。";
        }
        else
        {
            // 全体が 1 つにまとまるような画像では、感度を自動で上げて探し直す（上げた値はスライダーにも入れる）
            (rects, int used, int removed) = RegionDetect.DetectAuto(pair.Unlit, pair.Lit, asked, (int)GapSlider.Value, (int)ThresholdSlider.Maximum);
            _detectNote = (used != asked ? $"感度 {asked} では全体が 1 つにまとまるので、感度を {used} に上げて探しました。" : "")
                + (removed > 0 ? $"絵の大部分を覆う囲み {removed} 個は部品ではないので外しました。" : "");
            if (used != asked) ThresholdSlider.Value = used;
        }
        if (replace) _regions.Clear();
        int added = 0;
        foreach (var r in rects)
        {
            if (!replace && _regions.Any(x => !x.Rect.Intersect(r).IsEmpty)) continue;
            _regions.Add(new CropRegion { Rect = r, Name = NextName(), Layer = _layer, Set = pair.Set });
            added++;
        }
        Canvas.Selected.Clear();
        RefreshList();
        ShowProps();
        Canvas.InvalidateVisual();
        UpdateSummary();
        return added;
    }

    private void OnDetectReplace(object sender, RoutedEventArgs e)
    {
        if (_regions.Count > 0 && MessageBox.Show(this, $"今の囲み {_regions.Count} 個を消して、探し直しますか？（Ctrl+Z で戻せます）", Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Detect(replace: true);
    }

    private void OnDetectAdd(object sender, RoutedEventArgs e) => Detect(replace: false);

    /// <summary>選んだ囲みを、つなげずに探し直して分ける。戻り値は分けた後の数（分けられなかった囲みも含む）。</summary>
    public int SplitSelected()
    {
        if (BasisPair() is null || Canvas.Selected.Count == 0) return 0;
        bool night = (BasisCombo.SelectedItem as BasisItem)?.Night ?? true;
        var result = new List<CropRegion>();
        foreach (var r in Canvas.Selected.ToList())
        {
            // 囲みの切り抜き元（発光・扉など）と無発光の差で分ける
            if (PairFor(r.Set, night) is not { } pair) { result.Add(r); continue; }
            var parts = RegionDetect.Split(pair.Unlit, pair.Lit, r.Rect, (int)ThresholdSlider.Value);
            if (parts.Count < 2) { result.Add(r); continue; }
            int at = _regions.IndexOf(r);
            _regions.RemoveAt(at);
            for (int k = 0; k < parts.Count; k++)
            {
                var p = new CropRegion { Rect = parts[k], Name = $"{r.Name}_{k + 1}", Subject = r.Subject, Layer = r.Layer, Set = r.Set };
                _regions.Insert(at + k, p);
                result.Add(p);
            }
        }
        Canvas.Selected.Clear();
        foreach (var r in result) Canvas.Selected.Add(r);
        RefreshList();
        SyncListSelection();
        ShowProps();
        Canvas.InvalidateVisual();
        UpdateSummary();
        return result.Count;
    }

    private void OnSplit(object sender, RoutedEventArgs e)
    {
        int before = Canvas.Selected.Count;
        int after = SplitSelected();
        if (before > 0 && after == before)
            MessageBox.Show(this, "これ以上は分けられませんでした。「感度」を変えるか、何もない所をドラッグして手で囲んでください。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnMerge(object sender, RoutedEventArgs e)
    {
        var sel = _regions.Where(Canvas.Selected.Contains).ToList();
        if (sel.Count < 2) return;
        sel[0].Rect = RegionDetect.Union(sel.Select(r => r.Rect));
        foreach (var r in sel.Skip(1)) _regions.Remove(r);
        Canvas.Selected.Clear();
        Canvas.Selected.Add(sel[0]);
        RefreshList();
        SyncListSelection();
        ShowProps();
        Canvas.InvalidateVisual();
        UpdateSummary();
    }

    private void OnDeleteAllRegions(object sender, RoutedEventArgs e)
    {
        if (_regions.Count == 0) return;
        if (!MainWindow.IsTestRun && MessageBox.Show(this, $"囲み {_regions.Count} 個をすべて消しますか？（Ctrl+Z で戻せます）", Title,
                MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        DeleteAllRegions();
    }

    /// <summary>囲みの一覧で Delete: 選んだ囲みを消す。</summary>
    private void OnRegionListKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { OnDeleteRegions(this, new RoutedEventArgs()); e.Handled = true; }
    }

    /// <summary>囲みを全部消す（Ctrl+Z で戻せる）。</summary>
    public void DeleteAllRegions()
    {
        if (_regions.Count == 0) return;
        _regions.Clear();
        Canvas.Selected.Clear();
        RefreshList();
        ShowProps();
        Canvas.InvalidateVisual();
        UpdateSummary();
    }

    // ================= 元に戻す・やり直し（囲み） =================

    private sealed record RegionState(PsdRect Rect, string Name, string Subject, int Layer, string Set);
    private readonly List<List<RegionState>> _undo = [];
    private readonly List<List<RegionState>> _redo = [];
    private List<RegionState> _lastState = [];

    private List<RegionState> Snapshot() => _regions.Select(r => new RegionState(r.Rect, r.Name, r.Subject, r.Layer, r.Set)).ToList();

    /// <summary>囲みが変わっていれば、変わる前の状態を元に戻す履歴に積む（RefreshList のたびに呼ぶ）。</summary>
    private void RecordUndo()
    {
        var now = Snapshot();
        if (now.SequenceEqual(_lastState)) return;
        _undo.Add(_lastState);
        if (_undo.Count > 200) _undo.RemoveAt(0);
        _redo.Clear();
        _lastState = now;
    }

    private void Restore(List<RegionState> state)
    {
        _regions.Clear();
        _regions.AddRange(state.Select(s => new CropRegion { Rect = s.Rect, Name = s.Name, Subject = s.Subject, Layer = s.Layer, Set = s.Set }));
        _lastState = state;
        Canvas.Selected.Clear();
        _restoring = true;
        RefreshList();
        _restoring = false;
        ShowProps();
        Canvas.InvalidateVisual();
        UpdateSummary();
    }

    private bool _restoring;

    /// <summary>囲みの操作を 1 つ元に戻す。戻せなければ false。</summary>
    public bool UndoRegions()
    {
        if (_undo.Count == 0) return false;
        _redo.Add(_lastState);
        var s = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Restore(s);
        return true;
    }

    /// <summary>元に戻した操作をやり直す。</summary>
    public bool RedoRegions()
    {
        if (_redo.Count == 0) return false;
        _undo.Add(_lastState);
        var s = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Restore(s);
        return true;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        // 文字を入れている欄では、その欄の元に戻すを使う
        if (Keyboard.FocusedElement is TextBox || (Keyboard.FocusedElement is ComboBox cb && cb.IsEditable)) return;
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.Z) { CommitProps(); UndoRegions(); e.Handled = true; }
        else if (e.Key == Key.Y) { RedoRegions(); e.Handled = true; }
    }

    private void OnDeleteRegions(object sender, RoutedEventArgs e)
    {
        if (Canvas.Selected.Count == 0) return;
        _regions.RemoveAll(Canvas.Selected.Contains);
        Canvas.Selected.Clear();
        RefreshList();
        ShowProps();
        Canvas.InvalidateVisual();
        UpdateSummary();
    }

    // ================= 一覧 =================

    private void RefreshList()
    {
        if (!_restoring) RecordUndo();
        if (CropSources.Any()) RefreshOverlayRows();
        _suppress = true;
        RegionList.ItemsSource = _regions.Select(r => new RegionRow
        {
            Region = r,
            Text = $"{r.Name}　{(r.Subject.Length == 0 ? "（状態量なし）" : string.Equals(r.Subject, "true", StringComparison.OrdinalIgnoreCase) ? "常表示" : r.Subject)}　{(r.Set != CropSetNames.Lit ? $"[{r.Set}]　" : "")}{r.Rect.Width}×{r.Rect.Height}",
        }).ToList();
        _suppress = false;
        SyncListSelection();
        UpdateSummary();
    }

    private void SyncListSelection()
    {
        _suppress = true;
        RegionList.SelectedItems.Clear();
        if (RegionList.ItemsSource is List<RegionRow> rows)
            foreach (var row in rows.Where(r => Canvas.Selected.Contains(r.Region))) RegionList.SelectedItems.Add(row);
        if (RegionList.SelectedItems.Count > 0) RegionList.ScrollIntoView(RegionList.SelectedItems[0]);
        _suppress = false;
    }

    private void OnListSelection(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        Canvas.Selected.Clear();
        foreach (RegionRow row in RegionList.SelectedItems) Canvas.Selected.Add(row.Region);
        if (Canvas.Selected.Count > 0) Canvas.Reveal(Canvas.Selected.First().Rect);
        Canvas.InvalidateVisual();
        ShowProps();
    }

    private string _detectNote = "";

    private void UpdateSummary()
    {
        int noSubject = _regions.Count(r => r.Subject.Length == 0);
        SummaryText.Text = _detectNote + $"囲み {_regions.Count} 個" + (noSubject > 0 ? $"（状態量なし {noSubject} 個。あとでパネル画面でも設定できます）" : "");
    }

    /// <summary>確認用: 感度ごとの囲みの数と、いちばん大きい囲みの大きさ（絵全体に対する %）。</summary>
    public string DetectProbeForTest(int[] thresholds, int gap)
    {
        if (BasisPair() is not { } pair) return "";
        long canvas = (long)pair.Unlit.Width * pair.Unlit.Height;
        return string.Join(" / ", thresholds.Select(t =>
        {
            var rs = RegionDetect.Detect(pair.Unlit, pair.Lit, t, gap);
            var big = rs.OrderByDescending(r => (long)r.Width * r.Height).FirstOrDefault();
            return $"感度 {t}: {rs.Count} 個・最大 {big.Width}×{big.Height}（{(rs.Count > 0 ? 100.0 * big.Width * big.Height / canvas : 0):0}%）";
        }));
    }

    /// <summary>確認用: 直前に「部品を探す」で感度を上げたときの知らせ。</summary>
    public string DetectNoteForTest => _detectNote;

    // ================= 選んだ囲みの設定 =================

    private bool _loadingProps;

    private void ShowProps()
    {
        var sel = _regions.Where(Canvas.Selected.Contains).ToList();
        _loadingProps = true;
        PropGrid.IsEnabled = sel.Count > 0;
        NameBox.IsEnabled = RectBox.IsEnabled = sel.Count == 1;
        if (sel.Count == 0)
        {
            PropTitle.Text = "囲みを選んでください";
            NameBox.Text = RectBox.Text = LayerBox.Text = SubjectBox.Text = "";
            DayPreview.Source = NightPreview.Source = null;
        }
        else
        {
            PropTitle.Text = sel.Count == 1 ? sel[0].Name : $"{sel.Count} 個選択中（まとめて設定）";
            NameBox.Text = sel.Count == 1 ? sel[0].Name : "";
            SubjectBox.Text = sel.Select(r => r.Subject).Distinct().Count() == 1 ? sel[0].Subject : "";
            LayerBox.Text = sel.Select(r => r.Layer).Distinct().Count() == 1 ? sel[0].Layer.ToString() : "";
            SetBox.SelectedItem = sel.Select(r => r.Set).Distinct().Count() == 1 ? sel[0].Set : null;
            var r0 = sel[0].Rect;
            RectBox.Text = sel.Count == 1 ? $"{r0.Left}, {r0.Top}, {r0.Width}, {r0.Height}" : "";
            var (day, night) = SetImages(sel[0].Set);
            DayPreview.Source = day != null ? ImageLoader.ToBitmap(RegionDetect.CropOf(day, r0)) : null;
            NightPreview.Source = night != null ? ImageLoader.ToBitmap(RegionDetect.CropOf(night, r0)) : null;
        }
        _loadingProps = false;
    }

    private void OnPropKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitProps();
    }

    private void OnPropCommit(object sender, RoutedEventArgs e) => CommitProps();
    private void OnPropCommitAny(object? sender, EventArgs e) => CommitProps();

    private void CommitProps()
    {
        if (_loadingProps) return;
        var sel = _regions.Where(Canvas.Selected.Contains).ToList();
        if (sel.Count == 0) return;
        bool changed = false;
        if (sel.Count == 1)
        {
            var name = NameBox.Text.Trim();
            if (name.Length > 0 && name != sel[0].Name) { sel[0].Name = name; changed = true; }
            var parts = RectBox.Text.Split(',').Select(s => int.TryParse(s.Trim(), out var v) ? v : (int?)null).ToList();
            if (parts.Count == 4 && parts.All(p => p != null) && parts[2] > 0 && parts[3] > 0)
            {
                var r = new PsdRect(parts[0]!.Value, parts[1]!.Value, parts[0]!.Value + parts[2]!.Value, parts[1]!.Value + parts[3]!.Value);
                if (r != sel[0].Rect) { sel[0].Rect = r; changed = true; }
            }
        }
        var subject = SubjectBox.Text.Trim();
        if (sel.Count == 1 || subject.Length > 0)
            foreach (var r in sel.Where(r => r.Subject != subject)) { r.Subject = subject; changed = true; }
        if (int.TryParse(LayerBox.Text.Trim(), out var layer) && layer >= 1)
            foreach (var r in sel.Where(r => r.Layer != layer)) { r.Layer = layer; changed = true; }
        if (!changed) return;
        RefreshList();
        ShowProps();
        Canvas.InvalidateVisual();
    }

    private void OnSetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProps || SetBox.SelectedItem is not string set) return;
        foreach (var r in _regions.Where(Canvas.Selected.Contains)) r.Set = set;
        RefreshList();
        ShowProps();
    }

    // ================= 下に敷く・上に重ねる画像（「重ねる画像」のタブ） =================

    /// <summary>重ね順の一覧（と部品用の一覧）の 1 行。Item が null の行は「表示灯」「メインパネル」の区切り（動かせない）。</summary>
    public sealed class OverlayRow
    {
        public OverlayItem? Item { get; init; }
        public bool IsMain { get; init; }
        public string Text { get; init; } = "";
        public FontWeight Weight => Item == null ? FontWeights.SemiBold : FontWeights.Normal;
        public Brush Brush => Item == null ? (IsMain ? Brushes.Firebrick : Brushes.DarkOrange) : Item.CropSource ? Brushes.DarkGreen : Brushes.Black;
    }

    private const string LampsRowText = "── 表示灯（切り抜いた囲み）──";
    private const string MainRowText = "── メインパネル（4 点の画像）──";
    private bool _suppressOverlay;
    private string? _viewBeforeOverlayTab;

    /// <summary>選んでいる画像（上の重ねる画像の一覧か、下の部品用の一覧）。</summary>
    private OverlayItem? SelectedOverlay => (OverlayList.SelectedItem as OverlayRow)?.Item ?? (PartsList.SelectedItem as OverlayRow)?.Item;

    /// <summary>重ね順（上ほど手前）: 上に重ねる画像（手前から）・表示灯・メインパネル・下に敷く画像（手前から）。区切りの行は null。</summary>
    private List<OverlayItem?> DisplayOrder()
    {
        var list = new List<OverlayItem?>();
        list.AddRange(_overlays.Where(x => !x.CropSource && !x.Overlay.Below).Reverse());
        list.Add(null); // 表示灯
        list.Add(null); // メインパネル
        list.AddRange(_overlays.Where(x => !x.CropSource && x.Overlay.Below).Reverse());
        return list;
    }

    /// <summary>重ね順（上ほど手前、区切りを含む）から _overlays（下に敷くもの → 上に重ねるもの、それぞれ奥から）を作り直す。</summary>
    private void ApplyDisplayOrder(List<OverlayItem?> order)
    {
        int lamps = order.IndexOf(null);
        var overs = order.Take(lamps).OfType<OverlayItem>().Reverse().ToList();
        var unders = order.Skip(lamps + 2).OfType<OverlayItem>().Reverse().ToList();
        foreach (var o in overs) o.Overlay.Below = false;
        foreach (var u in unders) u.Overlay.Below = true;
        var crops = CropSources.ToList();
        _overlays.Clear();
        _overlays.AddRange(unders);
        _overlays.AddRange(overs);
        _overlays.AddRange(crops);
    }

    private string OverlayText(OverlayItem x)
    {
        var o = x.Overlay;
        if (x.CropSource)
            return $"{o.Name}（{Path.GetFileName(o.DayPath)}{(o.NightPath != null ? $" / 夜 {Path.GetFileName(o.NightPath)}" : "")}・囲み {_regions.Count(r => r.Set == o.Name)} 個）";
        var layer = o.Layer?.ToString() ?? "自動";
        return $"{(o.Below ? "下" : "上")}: {o.Name}（{Path.GetFileName(o.DayPath)}・{o.X}, {o.Y}・レイヤー {layer}・{(o.Subject == "true" ? "常表示" : o.Subject)}）";
    }

    /// <summary>一覧・設定の欄・見本を作り直す。select を選び直す（null なら今の選択のまま）。</summary>
    private void RefreshOverlays(OverlayItem? select = null)
    {
        RefreshOverlayRows(select);
        ShowOverlayProps();
        RefreshCropSources();
        RefreshViews();
        UpdateOverlayFrame();
    }

    /// <summary>一覧の行だけを作り直す（囲みの数が変わったときなど。見本は作り直さない）。上は重ねる画像、下は部品用。</summary>
    private void RefreshOverlayRows(OverlayItem? select = null)
    {
        var keep = select ?? SelectedOverlay;
        var rows = new List<OverlayRow>();
        int sep = 0;
        foreach (var x in DisplayOrder())
        {
            if (x != null) rows.Add(new OverlayRow { Item = x, Text = OverlayText(x) });
            else rows.Add(new OverlayRow { IsMain = sep++ == 1, Text = sep == 1 ? LampsRowText : MainRowText });
        }
        var parts = CropSources.Select(x => new OverlayRow { Item = x, Text = OverlayText(x) }).ToList();
        _suppressOverlay = true;
        OverlayList.ItemsSource = rows;
        PartsList.ItemsSource = parts;
        OverlayList.SelectedItem = keep is { CropSource: false } ? rows.FirstOrDefault(r => r.Item == keep) : null;
        PartsList.SelectedItem = keep is { CropSource: true } ? parts.FirstOrDefault(r => r.Item == keep) : null;
        _suppressOverlay = false;
    }

    private void OnOverlayListSelection(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOverlay) return;
        // 上の一覧で選んだら、下の一覧の選択は外す（設定の欄は 1 つ）
        if (OverlayList.SelectedItem != null) { _suppressOverlay = true; PartsList.SelectedItem = null; _suppressOverlay = false; }
        ShowOverlayProps();
        UpdateOverlayFrame();
    }

    private void OnPartsListSelection(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOverlay) return;
        if (PartsList.SelectedItem != null) { _suppressOverlay = true; OverlayList.SelectedItem = null; _suppressOverlay = false; }
        ShowOverlayProps();
        UpdateOverlayFrame();
    }

    /// <summary>重ねる画像・部品用の一覧で Delete: 選んだ画像を外す。</summary>
    private void OnOverlayListKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && SelectedOverlay != null) { OnRemoveOverlay(this, new RoutedEventArgs()); e.Handled = true; }
    }

    // ---- 足す ----

    private void OnAddOverlayButton(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            AddToRecent = false, Multiselect = true, Title = "重ねる画像を選ぶ",
            Filter = "画像・PSD|*.png;*.bmp;*.jpg;*.jpeg;*.gif;*.tif;*.tiff;*.psd|すべて|*.*",
        };
        var known = _overlays.Select(x => x.Overlay.DayPath).Concat(_paths.OfType<string>()).FirstOrDefault();
        if (known != null) dlg.InitialDirectory = Path.GetDirectoryName(known);
        if (dlg.ShowDialog(this) != true) return;
        // 選んでいる画像と同じ側（そのすぐ手前）に。何も選んでいない・区切りの行なら下に敷く
        var near = SelectedOverlay is { CropSource: false } n ? n : null;
        AddOverlayFiles(dlg.FileNames, near?.Overlay.Below ?? true, near, crop: false);
    }

    /// <summary>部品用の画像を足す（下の一覧）。</summary>
    private void OnAddPartsButton(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            AddToRecent = false, Multiselect = true, Title = "部品用の画像を選ぶ",
            Filter = "画像・PSD|*.png;*.bmp;*.jpg;*.jpeg;*.gif;*.tif;*.tiff;*.psd|すべて|*.*",
        };
        var known = _overlays.Select(x => x.Overlay.DayPath).Concat(_paths.OfType<string>()).FirstOrDefault();
        if (known != null) dlg.InitialDirectory = Path.GetDirectoryName(known);
        if (dlg.ShowDialog(this) != true) return;
        AddOverlayFiles(dlg.FileNames, true, null, crop: true);
    }

    /// <summary>
    /// 画像（PSD ならレイヤーを振り分ける）を足す。位置 0, 0・レイヤー自動・状態量 true。夜の画像は名前から探す（_N など）。
    /// after があればそのすぐ手前に入れる。最後に足したものを選ぶ。
    /// </summary>
    public void AddOverlayFiles(IReadOnlyList<string> files, bool below, OverlayItem? after = null, bool crop = false)
    {
        OverlayItem? last = null;
        foreach (var f in files)
        {
            var item = LoadOverlay(f, below);
            if (item == null) continue;
            if (crop)
            {
                MakeCropSource(item);
                _overlays.Add(item);
                last = item;
                continue;
            }
            int at = after != null && _overlays.IndexOf(after) is var i && i >= 0 ? i + 1 : _overlays.Count;
            // 同じ側の並びの中で after の手前に入る（_overlays は下に敷くもの → 上に重ねるものの順）
            if (after == null && below) at = _overlays.Count(x => x.Overlay.Below);
            _overlays.Insert(Math.Min(at, _overlays.Count), item);
            after = item;
            last = item;
        }
        if (last != null)
        {
            LeftTabs.SelectedItem = OverlayTab;
            RefreshOverlays(last);
        }
    }

    private OverlayItem? LoadOverlay(string path, bool below)
    {
        var o = new CropOverlay { Name = Path.GetFileNameWithoutExtension(path), Below = below, DayPath = path };
        var item = new OverlayItem { Overlay = o };
        try
        {
            if (IsPsd(path))
            {
                var guess = Guess(path);
                var night = guess[NightUnlit] ?? guess[NightLit];
                bool nightPsd = night != null && IsPsd(night);
                var paths = new List<string> { path };
                if (nightPsd) paths.Add(night!);
                var aw = new LayerAssignWindow(this, paths, ["昼の画像", "夜の画像"], [new(false, false), new(true, false)],
                    [path, nightPsd ? night : path], [null, null], $"重ねる画像「{o.Name}」");
                if (aw.ShowDialog() != true || aw.Result?[0] is not { } day) return null;
                o.DayPath = day.PsdPath; o.DayLayers = day.Layers; o.Day = day.Pixels; item.DayPx = day.Pixels;
                if (aw.Result[1] is { } n) { o.NightPath = n.PsdPath; o.NightLayers = n.Layers; o.Night = n.Pixels; item.NightPx = n.Pixels; }
            }
            else
            {
                item.DayPx = ImageLoader.Load(path).Pixels;
                var guess = Guess(path);
                if ((guess[NightUnlit] ?? guess[NightLit]) is { } night && !IsPsd(night) && !string.Equals(night, path, StringComparison.OrdinalIgnoreCase))
                {
                    o.NightPath = night;
                    item.NightPx = ImageLoader.TryLoadPixels(night);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException or InvalidDataException or InvalidOperationException)
        {
            MessageBox.Show(this, $"「{Path.GetFileName(path)}」を読めませんでした:\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        return item;
    }

    // ---- ドロップ ----

    private static readonly string[] OverlayExtensions = [".png", ".bmp", ".jpg", ".jpeg", ".gif", ".tif", ".tiff", ".psd"];

    private void OnOverlayDragOver(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetData(DataFormats.FileDrop) is string[] fs && fs.Any(f => OverlayExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnOverlayDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] fs) return;
        var files = fs.Where(f => OverlayExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
        if (files.Count == 0) return;
        // 落とした行: メインパネルの行より上なら上に重ねる、下なら下に敷く
        var row = (e.OriginalSource as DependencyObject) is { } d && ItemsControl.ContainerFromElement(OverlayList, d) is ListBoxItem li ? li.DataContext as OverlayRow : null;
        var rows = (OverlayList.ItemsSource as List<OverlayRow>) ?? [];
        int mainAt = rows.FindIndex(r => r.IsMain);
        int at = row != null ? rows.IndexOf(row) : rows.Count; // 行のない所（いちばん下）なら下に敷く
        bool below = at > mainAt;
        // ドロップの処理中にダイアログ（PSD の振り分け）を出さないよう、終えてから足す
        Dispatcher.BeginInvoke(() => DropOverlayFiles(files, below, row?.Item), System.Windows.Threading.DispatcherPriority.Background);
        e.Handled = true;
    }

    /// <summary>部品用の一覧に落とした: 部品用として足す。</summary>
    private void OnPartsDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] fs) return;
        var files = fs.Where(f => OverlayExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
        if (files.Count == 0) return;
        Dispatcher.BeginInvoke(() => AddOverlayFiles(files, true, null, crop: true), System.Windows.Threading.DispatcherPriority.Background);
        e.Handled = true;
    }

    /// <summary>ドロップした画像を足す。落とした行の画像があり同じ側なら、そのすぐ手前に入れる。</summary>
    public void DropOverlayFiles(IReadOnlyList<string> files, bool below, OverlayItem? onto) =>
        AddOverlayFiles(files, below, onto != null && onto.Overlay.Below == below ? onto : null);

    // ---- 順番・消す ----

    private void OnOverlayUp(object sender, RoutedEventArgs e) => MoveOverlay(-1);
    private void OnOverlayDown(object sender, RoutedEventArgs e) => MoveOverlay(1);

    /// <summary>重ね順で 1 つ手前（-1）・奥（+1）へ。表示灯・メインパネルの区切りはまとめて飛び越えて、下に敷く⇔上に重ねるを入れ替える。</summary>
    public void MoveOverlay(int delta)
    {
        if (SelectedOverlay is not { } item || item.CropSource) return; // 切り抜く元は重ね順に入らない
        var order = DisplayOrder();
        int i = order.IndexOf(item);
        int j = i + delta;
        if (j < 0 || j >= order.Count) return;
        order.RemoveAt(i);
        // 区切り（表示灯・メインパネルの 2 行）にぶつかったら、その向こうへ
        int lamps = order.IndexOf(null);
        if (delta < 0 && j == lamps + 1) j = lamps;         // 下から上へ: 表示灯の行のすぐ上
        else if (delta > 0 && j == lamps + 1) j = lamps + 2; // 上から下へ: メインパネルの行のすぐ下
        order.Insert(Math.Clamp(j, 0, order.Count), item);
        ApplyDisplayOrder(order);
        RefreshOverlays(item);
    }

    private void OnRemoveOverlay(object sender, RoutedEventArgs e)
    {
        if (SelectedOverlay is not { } item) return;
        if (item.CropSource)
        {
            // 切り抜く元を消したら、それを使っていた囲みは「発光」に戻す
            foreach (var r in _regions.Where(r => r.Set == item.Overlay.Name)) r.Set = CropSetNames.Lit;
            _overlays.Remove(item);
            RefreshOverlays(CropSources.FirstOrDefault());
            RefreshList();
            ShowProps();
            return;
        }
        var order = DisplayOrder();
        int i = order.IndexOf(item);
        _overlays.Remove(item);
        // 消したあとは、その隣の画像を選ぶ
        var next = order.Skip(i + 1).OfType<OverlayItem>().FirstOrDefault() ?? order.Take(i).OfType<OverlayItem>().LastOrDefault();
        RefreshOverlays(next);
    }

    // ---- 選んだ画像の設定 ----

    private bool _loadingOverlayProps;

    private void ShowOverlayProps()
    {
        var x = SelectedOverlay;
        _loadingOverlayProps = true;
        OverlayProps.IsEnabled = x != null;
        OverlayProps.Header = x == null ? "選んだ画像（一覧で画像を選んでください）"
            : x.CropSource ? "選んだ画像（部品用）" : x.Overlay.Below ? "選んだ画像（下に敷く）" : "選んだ画像（上に重ねる）";
        OvLayerOnly.IsChecked = x is { LayerOnly: true };
        OvLayerOnly.IsEnabled = x is { CropSource: true };
        OvLayerOnly.Visibility = x is { CropSource: false } ? Visibility.Collapsed : Visibility.Visible;
        // 切り抜く元は位置 0, 0（4 点の画像と同じ座標）・レイヤーと状態量は囲みごと
        OvX.IsEnabled = OvY.IsEnabled = OvLayer.IsEnabled = OvSubject.IsEnabled = x is { CropSource: false };
        if (OvSubject.ItemsSource == null) OvSubject.ItemsSource = new[] { "true" }.Concat(Subjects.Suggestions()).ToList();
        var o = x?.Overlay;
        OvFile.Text = o == null ? "" : Path.GetFileName(o.DayPath) + (o.DayLayers != null ? $"（レイヤー {o.DayLayers.Count} 個を重ねる）" : "");
        OvFile.ToolTip = o?.DayPath;
        OvName.Text = o?.Name ?? "";
        OvX.Text = o?.X.ToString() ?? "";
        OvY.Text = o?.Y.ToString() ?? "";
        OvLayer.Text = o?.Layer?.ToString() ?? "";
        OvSubject.Text = o?.Subject ?? "";
        OvNight.Text = o == null ? "" : o.NightPath != null ? Path.GetFileName(o.NightPath) + (o.NightLayers != null ? "（レイヤー）" : "") : "（なし。夜も昼の画像を使う）";
        OvNightPick.IsEnabled = OvNightClear.IsEnabled = o != null && o.NightLayers == null;
        _loadingOverlayProps = false;
    }

    private void OnOverlayPropKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitOverlayProps(); e.Handled = true; }
    }

    private void OnOverlayPropCommit(object sender, RoutedEventArgs e) => CommitOverlayProps();
    private void OnOverlayPropCommitAny(object? sender, EventArgs e) => CommitOverlayProps();

    private void CommitOverlayProps()
    {
        if (_loadingOverlayProps || SelectedOverlay is not { } x) return;
        var o = x.Overlay;
        bool moved = false;
        var newName = OvName.Text.Trim().Length > 0 ? OvName.Text.Trim() : Path.GetFileNameWithoutExtension(o.DayPath);
        if (x.CropSource && newName != o.Name)
        {
            // 切り抜く元の名前は組の名前なので、ほかと重ならないようにし、囲みの「切り抜き元」も付け替える
            var old = o.Name;
            o.Name = UniqueCropName(newName, x);
            foreach (var r in _regions.Where(r => r.Set == old)) r.Set = o.Name;
            RefreshOverlays(x);
            RefreshList();
            ShowProps();
            return;
        }
        o.Name = newName;
        if (x.CropSource) { RefreshOverlayRows(x); ShowOverlayProps(); return; }
        if (int.TryParse(OvX.Text.Trim(), out var nx) && nx != o.X) { o.X = nx; moved = true; }
        if (int.TryParse(OvY.Text.Trim(), out var ny) && ny != o.Y) { o.Y = ny; moved = true; }
        var lt = OvLayer.Text.Trim();
        o.Layer = lt.Length == 0 ? null : int.TryParse(lt, out var l) && l >= 0 ? l : o.Layer;
        o.Subject = OvSubject.Text.Trim().Length > 0 ? OvSubject.Text.Trim() : "true";
        if (moved) RefreshOverlays(x);
        else
        {
            // 見本は変わらないので、一覧の文字と設定の欄だけ直す
            _suppressOverlay = true;
            var rows = (List<OverlayRow>)OverlayList.ItemsSource;
            int i = rows.FindIndex(r => r.Item == x);
            if (i >= 0) { rows[i] = new OverlayRow { Item = x, Text = OverlayText(x) }; OverlayList.Items.Refresh(); OverlayList.SelectedIndex = i; }
            _suppressOverlay = false;
            ShowOverlayProps();
            UpdateOverlayFrame();
        }
    }

    /// <summary>「そのレイヤーのみを切り抜く」を変えた: 中央の表示と囲みの見本を作り直す。</summary>
    private void OnLayerOnlyChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOverlayProps || SelectedOverlay is not { CropSource: true } x) return;
        x.LayerOnly = OvLayerOnly.IsChecked == true;
        RefreshViews();
        ShowProps();
    }

    /// <summary>部品用にする: 位置 0, 0・名前を組の名前として重ならないように。大きさが 4 点の画像と違えば知らせる。</summary>
    private void MakeCropSource(OverlayItem x)
    {
        x.CropSource = true;
        x.Overlay.X = x.Overlay.Y = 0;
        x.Overlay.Name = UniqueCropName(x.Overlay.Name, x);
        var basePx = _px.FirstOrDefault(p => p != null);
        if (basePx != null && x.DayPx != null && (x.DayPx.Width != basePx.Width || x.DayPx.Height != basePx.Height) && !MainWindow.IsTestRun)
            MessageBox.Show(this, $"「{x.Overlay.Name}」の画像の大きさ（{x.DayPx.Width}×{x.DayPx.Height}）が、4 点の画像（{basePx.Width}×{basePx.Height}）と違います。\n部品は左上をそろえて切り抜くので、位置がずれるかもしれません。",
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>切り抜き元の組の名前: 「発光」「本体」「パネル本体」「重ね …」やほかの切り抜く元と重ならないようにする。</summary>
    private string UniqueCropName(string name, OverlayItem self)
    {
        var baseName = Regex.Replace(name, @"[:：|=;#]", "").Trim();
        if (baseName.Length == 0) baseName = "切り抜き元";
        bool Taken(string n) => n == CropSetNames.Lit || n == CropSetNames.Base || n == CropSetNames.PanelBody || n.StartsWith("重ね ")
            || CropSources.Any(c => c != self && c.Overlay.Name == n);
        var result = baseName;
        for (int i = 2; Taken(result); i++) result = $"{baseName}_{i}";
        return result;
    }

    private void OnOverlayNightPick(object sender, RoutedEventArgs e)
    {
        if (SelectedOverlay is not { } x) return;
        var dlg = new OpenFileDialog { AddToRecent = false, Title = "夜の画像を選ぶ", Filter = "画像|*.png;*.bmp;*.jpg;*.jpeg;*.gif;*.tif;*.tiff|すべて|*.*", InitialDirectory = Path.GetDirectoryName(x.Overlay.DayPath) };
        if (dlg.ShowDialog(this) != true) return;
        x.Overlay.NightPath = dlg.FileName;
        x.NightPx = ImageLoader.TryLoadPixels(dlg.FileName);
        RefreshOverlays(x);
    }

    private void OnOverlayNightClear(object sender, RoutedEventArgs e)
    {
        if (SelectedOverlay is not { } x) return;
        x.Overlay.NightPath = null;
        x.NightPx = null;
        RefreshOverlays(x);
    }

    // ---- 見本の上で動かす ----

    private void OnLeftTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != LeftTabs) return;
        bool overlay = LeftTabs.SelectedItem == OverlayTab;
        if (overlay == Canvas.OverlayMode) return;
        Canvas.OverlayMode = overlay;
        if (overlay)
        {
            // 重ねた見本を出す（タブを戻すと前の表示に戻す）
            _viewBeforeOverlayTab = (ViewCombo.SelectedItem as ViewItem)?.Label;
            if (!IsStackedView()) ShowViewForTest(StackedDayLabel);
        }
        else if (_viewBeforeOverlayTab != null && IsStackedView()) ShowViewForTest(_viewBeforeOverlayTab);
        // 右の欄は囲みの設定なので、重ねる画像のタブの間はそう知らせる
        PropGrid.Visibility = overlay ? Visibility.Collapsed : Visibility.Visible;
        if (overlay) PropTitle.Text = "重ねる画像は、左の「選んだ画像」の欄で設定します";
        else ShowProps();
        UpdateOverlayFrame();
    }

    private const string StackedDayLabel = "重ねた見本（昼）";
    private bool IsStackedView() => (ViewCombo.SelectedItem as ViewItem)?.Label.StartsWith("重ねた見本") == true;
    private bool IsNightStackedView() => (ViewCombo.SelectedItem as ViewItem)?.Label.StartsWith("重ねた見本（夜）") == true;

    /// <summary>選んだ画像の範囲を見本に描く（重ねる画像のタブのときだけ）。</summary>
    private void UpdateOverlayFrame()
    {
        var x = Canvas.OverlayMode && SelectedOverlay is { CropSource: false } sel ? sel : null;
        var px = x == null ? null : IsNightStackedView() ? x.NightPx ?? x.DayPx : x.DayPx;
        Canvas.OverlayRect = x != null && px != null ? new PsdRect(x.Overlay.X, x.Overlay.Y, x.Overlay.X + px.Width, x.Overlay.Y + px.Height) : null;
        Canvas.OverlayLabel = x?.Overlay.Name ?? "";
        Canvas.InvalidateVisual();
    }

    private void OnOverlayDragStarted()
    {
        if (SelectedOverlay is not { CropSource: false } x || _px[DayUnlit] == null) return;
        bool night = IsNightStackedView();
        // 動かしている画像を除いた見本の上に、その画像だけを浮かせて描く
        Canvas.Image = ImageLoader.ToBitmap(Stacked(night, exclude: x));
        var px = night ? x.NightPx ?? x.DayPx : x.DayPx;
        Canvas.OverlayFloating = px != null ? ImageLoader.ToBitmap(px) : null;
    }

    private void OnOverlayDragging(int dx, int dy)
    {
        if (SelectedOverlay is not { } x) return;
        _loadingOverlayProps = true;
        OvX.Text = (x.Overlay.X + dx).ToString();
        OvY.Text = (x.Overlay.Y + dy).ToString();
        _loadingOverlayProps = false;
    }

    private void OnOverlayDragFinished(int dx, int dy)
    {
        Canvas.OverlayFloating = null;
        if (SelectedOverlay is not { } x) { ShowView(); return; }
        MoveSelectedOverlay(dx, dy);
    }

    /// <summary>選んだ画像を dx, dy だけ動かす（ドラッグ・矢印キー）。</summary>
    public void MoveSelectedOverlay(int dx, int dy)
    {
        if (SelectedOverlay is not { CropSource: false } x) return;
        x.Overlay.X += dx;
        x.Overlay.Y += dy;
        RefreshOverlays(x);
    }

    /// <summary>見本の枠の外をクリックした: その点にある、いちばん手前の重ねる画像を選ぶ（なければ選択を外す）。</summary>
    private void OnOverlayPicked(Point p)
    {
        bool night = IsNightStackedView();
        foreach (var x in DisplayOrder().OfType<OverlayItem>())
        {
            var px = night ? x.NightPx ?? x.DayPx : x.DayPx;
            if (px == null) continue;
            int ix = (int)Math.Floor(p.X) - x.Overlay.X, iy = (int)Math.Floor(p.Y) - x.Overlay.Y;
            if (ix < 0 || iy < 0 || ix >= px.Width || iy >= px.Height || px.Bgra[(iy * px.Width + ix) * 4 + 3] < 32) continue;
            OverlayList.SelectedItem = ((List<OverlayRow>)OverlayList.ItemsSource).First(r => r.Item == x);
            return;
        }
        OverlayList.SelectedItem = null;
    }

    /// <summary>重ねた見本: 下に敷く画像（奥から）→ メインパネル（4 点の画像）→ 上に重ねる画像。どれも X・Y のとおりに置く。exclude は描かない。</summary>
    private PsdPixels Stacked(bool night, OverlayItem? exclude = null)
    {
        var baseImg = (night ? _px[NightUnlit] : null) ?? _px[DayUnlit]!;
        int w = baseImg.Width, h = baseImg.Height;
        var acc = new byte[w * h * 4];
        void Over(PsdPixels? img, int ox, int oy)
        {
            if (img == null) return;
            for (int y = 0; y < img.Height; y++)
            {
                int ty = y + oy;
                if (ty < 0 || ty >= h) continue;
                for (int x = 0; x < img.Width; x++)
                {
                    int tx = x + ox;
                    if (tx < 0 || tx >= w) continue;
                    int si = (y * img.Width + x) * 4, di = (ty * w + tx) * 4;
                    int sa = img.Bgra[si + 3];
                    if (sa == 0) continue;
                    int da = acc[di + 3], oa = sa + da * (255 - sa) / 255;
                    for (int c = 0; c < 3; c++)
                        acc[di + c] = (byte)(oa == 0 ? 0 : (img.Bgra[si + c] * sa + acc[di + c] * da * (255 - sa) / 255) / oa);
                    acc[di + 3] = (byte)oa;
                }
            }
        }
        PsdPixels? Pick(OverlayItem x) => night ? x.NightPx ?? x.DayPx : x.DayPx;
        foreach (var x in _overlays.Where(x => !x.CropSource && x.Overlay.Below && x != exclude)) Over(Pick(x), x.Overlay.X, x.Overlay.Y);
        Over(baseImg, 0, 0);
        foreach (var x in _overlays.Where(x => !x.CropSource && !x.Overlay.Below && x != exclude)) Over(Pick(x), x.Overlay.X, x.Overlay.Y);
        return new PsdPixels(new PsdRect(0, 0, w, h), acc);
    }

    // ================= 決定 =================

    private void OnOk(object sender, RoutedEventArgs e)
    {
        CommitProps();
        if (_px[DayUnlit] == null || _paths[DayUnlit] == null)
        {
            MessageBox.Show(this, "昼・無発光の画像（パネル本体の画像）を選んでください。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_regions.Count > 0 && _px[DayLit] == null && _regions.Any(r => r.Set == CropSetNames.Lit))
        {
            MessageBox.Show(this, "表示灯を切り抜く昼・発光の画像を選んでください。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!AskEmptySubjects()) return;
        Spec = MakeSpec();
        DialogResult = true;
    }

    /// <summary>
    /// 状態量（Subject）のない囲みがあれば、常表示（true）にするか、点灯を確かめるため ats99 に仮設定するか聞く
    /// （どちらも Ctrl+Z で戻せる）。やめたら false（画面に戻る）。
    /// </summary>
    public const string TemporarySubject = "ats99";

    private bool AskEmptySubjects()
    {
        var empty = _regions.Where(r => r.Subject.Trim().Length == 0).ToList();
        if (empty.Count == 0) return true;
        var names = string.Join("、", empty.Take(8).Select(r => r.Name)) + (empty.Count > 8 ? $" …ほか {empty.Count - 8} 個" : "");
        int answer = ChoiceDialog.Show(this, "状態量のない表示灯",
            $"状態量（Subject）のない囲みが {empty.Count} 個あります（{names}）。\n状態量がないと、BVE では表示されません。どうしますか？",
            [
                $"常表示（Subject = true）にして作る（{empty.Count} 個）",
                $"点灯を確かめるため、Subject = {TemporarySubject} に仮設定して作る（動作プレビューの {TemporarySubject} で点灯・消灯を切り替えられます。あとで本来の状態量に直してください）",
                "やめて、この画面に戻る",
            ]);
        if (answer is < 0 or 2) return false;
        foreach (var r in empty) r.Subject = answer == 0 ? "true" : TemporarySubject;
        RefreshList();
        ShowProps();
        return true;
    }

    private CropBuildSpec MakeSpec()
    {
        var sets = new List<CropSetImages>();
        if (_px[DayLit] != null)
            sets.Add(new CropSetImages
            {
                Name = CropSetNames.Lit, DayPath = _paths[DayLit]!, Day = _px[DayLit]!, DayLayers = _layers[DayLit],
                NightPath = _paths[NightLit], Night = _px[NightLit], NightLayers = _layers[NightLit],
            });
        // 部品を切り抜く元にした重ねる画像は、切り抜き元の組にする（重ねない）
        // そのレイヤーのみでなければ、透明な所へメインパネルを重ねた絵から切り抜く（パネルにも「メインパネルに重ねる」と書く）
        foreach (var x in CropSources.Where(c => c.DayPx != null))
        {
            var (day, night) = SetImages(x.Overlay.Name);
            sets.Add(new CropSetImages
            {
                Name = x.Overlay.Name, DayPath = x.Overlay.DayPath, Day = day!, DayLayers = x.Overlay.DayLayers,
                NightPath = x.NightPx != null ? x.Overlay.NightPath : null, Night = night, NightLayers = x.NightPx != null ? x.Overlay.NightLayers : null,
                OverMain = !x.LayerOnly && (day != x.DayPx || night != x.NightPx),
            });
        }
        var outDir = OutBox.Text.Trim();
        return new CropBuildSpec
        {
            PanelDir = _panelDir,
            OutputDir = Path.GetFullPath(Path.IsPathRooted(outDir) ? outDir : Path.Combine(_panelDir, outDir.Length > 0 ? outDir : "crop")),
            UnlitDayPath = _paths[DayUnlit]!,
            UnlitDayLayers = _layers[DayUnlit],
            UnlitDay = _layers[DayUnlit] != null ? _px[DayUnlit] : null,
            UnlitNightPath = _paths[NightUnlit],
            UnlitNightLayers = _layers[NightUnlit],
            UnlitNight = _layers[NightUnlit] != null ? _px[NightUnlit] : null,
            Width = _px[DayUnlit]!.Width,
            Height = _px[DayUnlit]!.Height,
            Sets = sets,
            Regions = _regions.ToList(),
            Overlays = _overlays.Where(x => !x.CropSource).Select(x => x.Overlay).ToList(),
        };
    }

    // ================= 確認用 =================

    public IReadOnlyList<CropRegion> Regions => _regions;

    public void SelectForTest(IEnumerable<CropRegion> regions)
    {
        Canvas.Selected.Clear();
        foreach (var r in regions) Canvas.Selected.Add(r);
        SyncListSelection();
        ShowProps();
        Canvas.InvalidateVisual();
    }

    /// <summary>確認用: ［パネルを作る］と同じ指定を作る（窓は閉じない）。</summary>
    public CropBuildSpec? MakeSpecForTest() => _px[DayUnlit] == null ? null : MakeSpec();

    /// <summary>確認用: 振り分ける画面を、AssignPsd と同じ初期値で作る（表示は呼ぶ側）。</summary>
    public LayerAssignWindow OpenAssignForTest(int slot, string path)
    {
        var (paths, colPsd, colLayers) = AssignInput(slot, path);
        return new LayerAssignWindow(this, paths, SlotLabels, SlotKinds, colPsd, colLayers);
    }

    public void ApplyPicksForTest(LayerPick?[] picks) => ApplyPicks(picks);

    public string SlotTextForTest(int slot) => SlotBox(slot).Text;

    /// <summary>確認用: 重ねる画像を画面なしで足す。</summary>
    public void AddOverlayForTest(string path, bool below, int x, int y, int? layer = null, string subject = "true")
    {
        var o = new CropOverlay { Name = Path.GetFileNameWithoutExtension(path), Below = below, DayPath = path, X = x, Y = y, Layer = layer, Subject = subject };
        _overlays.Add(new OverlayItem { Overlay = o, DayPx = ImageLoader.Load(path).Pixels });
        RefreshOverlays();
    }

    /// <summary>確認用: 自動で探す感度・まとめる距離。</summary>
    public void SetDetectForTest(int threshold, int gap) { ThresholdSlider.Value = threshold; GapSlider.Value = gap; }
    public (double Threshold, double Gap) DetectSettingsForTest => (ThresholdSlider.Value, GapSlider.Value);

    /// <summary>確認用: 重ね順の一覧の文字（上ほど手前）。</summary>
    public List<string> OverlayRowsForTest => ((List<OverlayRow>)OverlayList.ItemsSource).Select(r => r.Item?.Overlay.Name ?? (r.IsMain ? "メインパネル" : "表示灯")).ToList();
    public List<string> PartsRowsForTest => ((List<OverlayRow>)PartsList.ItemsSource).Select(r => r.Item!.Overlay.Name).ToList();
    /// <summary>確認用: 選んだ画像の使い方を変える。</summary>
    public bool SelectedIsCropSourceForTest => SelectedOverlay?.CropSource == true;
    public bool AskEmptySubjectsForTest() => AskEmptySubjects();
    public void SetLayerOnlyForTest(bool on) => OvLayerOnly.IsChecked = on;
    public OverlayItem Sets(string name) => CropSources.First(x => x.Overlay.Name == name);
    public bool LayerOnlyEnabledForTest => OvLayerOnly.IsEnabled;
    public void SetSelectedSetForTest(string set) { ShowProps(); SetBox.SelectedItem = set; CommitProps(); }
    public void RenameSelectedOverlayForTest(string name) { OvName.Text = name; CommitOverlayProps(); }
    public void SetOverlayNightForTest(string path)
    {
        if (SelectedOverlay is not { } x) return;
        x.Overlay.NightPath = path;
        x.NightPx = ImageLoader.TryLoadPixels(path);
        RefreshOverlays(x);
    }
    /// <summary>確認用: 「部品を探す」の基準をラベルの始まりで選ぶ。</summary>
    public void SetSimpleDiffForTest(bool on) => SimpleDiffCheck.IsChecked = on;
    public bool SimpleDiffEnabledForTest => SimpleDiffCheck.IsEnabled;
    public void SelectBasisForTest(string labelStart) => BasisCombo.SelectedItem = ((List<BasisItem>)BasisCombo.ItemsSource).First(b => b.Label.StartsWith(labelStart));
    public List<string> BasisLabelsForTest => ((List<BasisItem>)BasisCombo.ItemsSource).Select(b => b.Label).ToList();
    public void SelectOverlayForTest(string name)
    {
        if (((List<OverlayRow>)OverlayList.ItemsSource).FirstOrDefault(r => r.Item?.Overlay.Name == name) is { } row) OverlayList.SelectedItem = row;
        else PartsList.SelectedItem = ((List<OverlayRow>)PartsList.ItemsSource).First(r => r.Item?.Overlay.Name == name);
    }
    public void RemoveSelectedOverlayByKeyForTest() => OnOverlayListKey(this, new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, 0, Key.Delete) { RoutedEvent = Keyboard.KeyDownEvent });
    public void DeleteRegionsByKeyForTest() => OnRegionListKey(this, new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, 0, Key.Delete) { RoutedEvent = Keyboard.KeyDownEvent });
    public string NextNameForTest() => NextName();
    public object? OverlaySelectedRowForTest => OverlayList.SelectedItem;
    public CropOverlay? SelectedOverlayForTest => SelectedOverlay?.Overlay;
    public void ShowOverlayTabForTest() => LeftTabs.SelectedItem = OverlayTab;
    public void ShowRegionTabForTest() => LeftTabs.SelectedIndex = 0;
    public (string X, string Y) OverlayPropsForTest => (OvX.Text, OvY.Text);
    public bool OverlayModeForTest => Canvas.OverlayMode && Canvas.OverlayRect != null;
    /// <summary>確認用: 見本の上でのドラッグと同じ（始める・動かす・離す）。</summary>
    public void DragOverlayForTest(int dx, int dy) { OnOverlayDragStarted(); OnOverlayDragging(dx, dy); OnOverlayDragFinished(dx, dy); }
    public PsdPixels StackedForTest(bool night) => Stacked(night);
    public void RemoveSelectedOverlayForTest() => OnRemoveOverlay(this, new RoutedEventArgs());
    public void SetOverlayXForTest(string x) { OvX.Text = x; CommitOverlayProps(); }

    /// <summary>確認用: 表示をラベルの始まりで選ぶ。</summary>
    public void ShowViewForTest(string labelStart)
    {
        if (ViewCombo.ItemsSource is IEnumerable<ViewItem> items && items.FirstOrDefault(i => i.Label.StartsWith(labelStart)) is { } v) ViewCombo.SelectedItem = v;
    }
}
