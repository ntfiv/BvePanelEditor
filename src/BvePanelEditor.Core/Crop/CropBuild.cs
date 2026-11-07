using BvePanelEditor.Core.Psd;

namespace BvePanelEditor.Core.Crop;

/// <summary>切り抜く囲み 1 つ（表示灯 1 つになる）。</summary>
public sealed class CropRegion
{
    public PsdRect Rect;
    public string Name = "";
    public string Subject = "";
    public int Layer = 1;
    /// <summary>切り抜き元の組の名前（既定「発光」）。</summary>
    public string Set = CropSetNames.Lit;
}

public static class CropSetNames
{
    public const string Lit = "発光";
    /// <summary>パネル本体（PSD から重ねたときだけパネルに書く）。</summary>
    public const string Base = PanelDocument.CropSetBaseName;
    /// <summary>下に敷く画像があるとき、4 点の画像のパネル本体（表示灯として置く）の組。</summary>
    public const string PanelBody = "パネル本体";
}

/// <summary>切り抜き元の組の画像（読み込み済み）。パスはフルパス。</summary>
public sealed class CropSetImages
{
    public string Name = CropSetNames.Lit;
    public required string DayPath;
    public required PsdPixels Day;
    public string? NightPath;
    public PsdPixels? Night;
    /// <summary>DayPath が PSD で、選んだレイヤーを重ねたとき、そのレイヤー。画像ファイル（PSD の統合画像を含む）なら null。</summary>
    public List<string>? DayLayers;
    public List<string>? NightLayers;
    /// <summary>Day・Night は透明な所へメインパネルを重ねた絵（パネルの切り抜き元にもそう書き、更新でも重ねる）。</summary>
    public bool OverMain;
}

/// <summary>
/// パネル本体（4 点の画像）の下に敷く・上に重ねる画像（背景、ガラスの映り込み、計器のカバーなど）。
/// 常表示の表示灯（Subject = true）として置く。下に敷く画像のうちいちばん下のものは [This] にする。
/// </summary>
public sealed class CropOverlay
{
    public string Name = "";
    /// <summary>true: パネル本体の下に敷く。false: 上に重ねる。</summary>
    public bool Below;
    /// <summary>画像（フルパス）。PSD のレイヤーを重ねたものなら PSD のパス。</summary>
    public required string DayPath;
    public string? NightPath;
    /// <summary>PSD のレイヤーを重ねたとき、そのレイヤーと重ねた絵（PNG に書き出す）。画像ファイルなら null（その画像を指す）。</summary>
    public List<string>? DayLayers, NightLayers;
    public PsdPixels? Day, Night;
    /// <summary>置く位置（パネル本体の画像の座標）。</summary>
    public int X, Y;
    /// <summary>レイヤー。null なら自動（下に敷くものは 1 から順に、上に重ねるものは表示灯より上）。</summary>
    public int? Layer;
    public string Subject = "true";
}

public sealed class CropBuildSpec
{
    public required string PanelDir;
    public required string OutputDir;
    /// <summary>昼・無発光（パネル本体の昼画像にする）。</summary>
    public required string UnlitDayPath;
    /// <summary>夜・無発光（パネル本体の夜画像にする）。</summary>
    public string? UnlitNightPath;
    /// <summary>昼・無発光が PSD のレイヤーを重ねたものなら、そのレイヤーと重ねた絵（パネル本体の PNG として書き出す）。</summary>
    public List<string>? UnlitDayLayers;
    public PsdPixels? UnlitDay;
    public List<string>? UnlitNightLayers;
    public PsdPixels? UnlitNight;
    public int Width, Height;
    public List<CropSetImages> Sets = [];
    public List<CropRegion> Regions = [];
    /// <summary>下に敷く・上に重ねる画像（それぞれ下から順）。</summary>
    public List<CropOverlay> Overlays = [];
}

public sealed class CropBuildResult
{
    public List<PsdOutputFile> Files = [];
    public List<PanelSection> Sections = [];
    /// <summary>パネルに書く切り抜き元（パスはパネルからの相対）。</summary>
    public List<CropSet> Sets = [];
    /// <summary>パネル本体（メインパネルの表示灯）の昼・夜の画像。</summary>
    public string BaseDay = "";
    public string? BaseNight;
    /// <summary>メインパネルの表示灯（Sections の中のもの）。</summary>
    public PanelSection? Main;
    public List<string> Warnings = [];
}

/// <summary>4 点の画像（と追加の組）から、パネル本体と表示灯を作る。</summary>
public static class CropBuild
{
    public static CropBuildResult Build(CropBuildSpec spec)
    {
        var r = new CropBuildResult
        {
            BaseDay = PsdImport.PathFrom(spec.PanelDir, spec.UnlitDayPath),
            BaseNight = spec.UnlitNightPath != null ? PsdImport.PathFrom(spec.PanelDir, spec.UnlitNightPath) : null,
        };
        var namer = new OutputNamer();

        // パネル本体を PSD のレイヤーから重ねたときは、PNG に書き出してそれを指す
        bool dayLayered = spec.UnlitDayLayers != null && spec.UnlitDay != null;
        bool nightLayered = spec.UnlitNightPath != null && spec.UnlitNightLayers != null && spec.UnlitNight != null;
        if (dayLayered || nightLayered)
        {
            var baseName = namer.Next(Path.GetFileNameWithoutExtension(spec.UnlitDayPath));
            if (dayLayered)
            {
                var dp = Path.Combine(spec.OutputDir, baseName + ".png");
                r.Files.Add(new(dp, PngWriter.EncodePow2(spec.UnlitDay!)));
                r.BaseDay = PsdImport.PathFrom(spec.PanelDir, dp);
            }
            if (nightLayered)
            {
                var np = Path.Combine(spec.OutputDir, baseName + "_N.png");
                r.Files.Add(new(np, PngWriter.EncodePow2(spec.UnlitNight!)));
                r.BaseNight = PsdImport.PathFrom(spec.PanelDir, np);
            }
            r.Sets.Add(new CropSet
            {
                Name = CropSetNames.Base,
                Day = PsdImport.PathFrom(spec.PanelDir, spec.UnlitDayPath),
                DayLayers = dayLayered ? [.. spec.UnlitDayLayers!] : null,
                Night = spec.UnlitNightPath != null ? PsdImport.PathFrom(spec.PanelDir, spec.UnlitNightPath) : null,
                NightLayers = nightLayered ? [.. spec.UnlitNightLayers!] : null,
            });
        }
        // 元の画像をそのまま指すパネル本体は書き出さないので、2 の累乗でなければ知らせる
        bool pow2 = spec.Width == PngWriter.NextPow2(spec.Width) && spec.Height == PngWriter.NextPow2(spec.Height);
        if (!pow2 && (!dayLayered || (spec.UnlitNightPath != null && !nightLayered)))
            r.Warnings.Add($"パネル本体の画像（{spec.Width}×{spec.Height}）は、幅・高さが 2 の累乗ではありません。BVE で使うには、元の画像の大きさを直してください（表示灯の画像は 2 の累乗で書き出しました）");
        foreach (var s in spec.Sets)
            r.Sets.Add(new CropSet
            {
                Name = s.Name,
                Day = PsdImport.PathFrom(spec.PanelDir, s.DayPath),
                DayLayers = s.DayLayers != null ? [.. s.DayLayers] : null,
                Night = s.NightPath != null ? PsdImport.PathFrom(spec.PanelDir, s.NightPath) : null,
                NightLayers = s.NightPath != null && s.NightLayers != null ? [.. s.NightLayers] : null,
                OverMain = s.OverMain,
            });

        // [This] には画像を書かない（パネル本体はメインパネルの表示灯にする）
        // パネル本体（4 点の画像）を PSD から重ねたときのつながりは「パネル本体」（メインパネルの表示灯が使う）
        foreach (var c in r.Sets.Where(c => c.Name == CropSetNames.Base)) c.Name = CropSetNames.PanelBody;

        // 下に敷く画像（下から順に、レイヤー 1 から）・上に重ねる画像
        var unders = spec.Overlays.Where(o => o.Below).ToList();
        var overs = spec.Overlays.Where(o => !o.Below).ToList();
        var underSections = new List<PanelSection>();
        int layer = 1;
        foreach (var u in unders)
        {
            underSections.Add(OverlaySection(spec, u, namer, r, u.Layer ?? layer));
            layer = Math.Max(layer, (u.Layer ?? layer) + 1);
        }
        // 4 点の画像のパネル本体は、メインパネルの表示灯として下に敷いた画像の上に置く
        int baseLayer = layer;
        var b = MainPanel.Lamp(r.BaseDay, r.BaseNight, baseLayer);
        if (dayLayered || nightLayered) b.CropSet = CropSetNames.PanelBody;
        underSections.Add(b);
        r.Main = b;
        r.Sections.AddRange(underSections);

        var canvas = new PsdRect(0, 0, spec.Width, spec.Height);
        int n = 0;
        foreach (var reg in spec.Regions)
        {
            n++;
            var set = spec.Sets.FirstOrDefault(s => s.Name == reg.Set);
            if (set == null) { r.Warnings.Add($"「{reg.Name}」の切り抜き元「{reg.Set}」がありません"); continue; }
            var rect = reg.Rect.Intersect(canvas);
            if (rect.IsEmpty) { r.Warnings.Add($"「{reg.Name}」の囲みが画像の外にあります"); continue; }
            var title = reg.Name.Trim().Length > 0 ? reg.Name.Trim() : $"表示灯{n}";
            var name = namer.Next(title);

            var sec = PanelSection.Create(SectionKind.PilotLamp);
            sec.SetTitle(title);
            sec.Set("Subject", reg.Subject.Trim());
            sec.Set("Location", PanelValues.Point(rect.Left, rect.Top));
            var dp = Path.Combine(spec.OutputDir, name + ".png");
            r.Files.Add(new(dp, PngWriter.EncodePow2(RegionDetect.CropOf(set.Day, rect))));
            sec.Set("DaytimeImage", PsdImport.PathFrom(spec.PanelDir, dp));
            if (set.Night != null)
            {
                var np = Path.Combine(spec.OutputDir, name + "_N.png");
                r.Files.Add(new(np, PngWriter.EncodePow2(RegionDetect.CropOf(set.Night, rect))));
                sec.Set("NighttimeImage", PsdImport.PathFrom(spec.PanelDir, np));
            }
            // 表示灯はメインパネルより上（メインパネル以下のレイヤーなら、すぐ上にする）
            int lampLayer = Math.Max(Math.Max(1, reg.Layer), baseLayer + 1);
            sec.Set("Layer", lampLayer.ToString());
            sec.CropSet = set.Name;
            r.Sections.Add(sec);
        }

        // 上に重ねる画像は、表示灯よりも上
        int top = r.Sections.Select(s => (int)PanelValues.Double(s.Get("Layer"), 0)).DefaultIfEmpty(baseLayer).Max() + 1;
        foreach (var o in overs)
        {
            r.Sections.Add(OverlaySection(spec, o, namer, r, o.Layer ?? top));
            top = Math.Max(top, (o.Layer ?? top) + 1);
        }
        return r;
    }

    /// <summary>
    /// 重ねる画像の昼・夜のパス（パネルからの相対）。PSD のレイヤーを重ねたものは PNG に書き出してそれを指し、
    /// 切り抜き元から更新で作り直せるようにつながり（切り抜き元の組）を足す。setName は組の名前（null なら「重ね 名前」）。
    /// つながりは位置が 0, 0 のときだけ（PSD の左上をパネルの左上に合わせるので）。
    /// </summary>
    private static (string Day, string? Night, string? Set) OverlayImages(CropBuildSpec spec, CropOverlay o, OutputNamer namer, CropBuildResult r, string? setName = null)
    {
        string? name = null;
        string PathOf(string path, List<string>? layers, PsdPixels? px, bool night)
        {
            if (layers == null || px == null) return PsdImport.PathFrom(spec.PanelDir, path);
            name ??= namer.Next(o.Name.Trim().Length > 0 ? o.Name.Trim() : Path.GetFileNameWithoutExtension(path));
            var p = Path.Combine(spec.OutputDir, name + (night ? "_N" : "") + ".png");
            r.Files.Add(new(p, PngWriter.EncodePow2(px)));
            return PsdImport.PathFrom(spec.PanelDir, p);
        }
        var day = PathOf(o.DayPath, o.DayLayers, o.Day, false);
        var nightPath = o.NightPath != null ? PathOf(o.NightPath, o.NightLayers, o.Night, true) : null;
        bool layered = (o.DayLayers != null && o.Day != null) || (o.NightLayers != null && o.Night != null);
        if (!layered || (setName == null && (o.X != 0 || o.Y != 0))) return (day, nightPath, null);
        setName ??= $"重ね {name}";
        r.Sets.RemoveAll(c => c.Name == setName);
        r.Sets.Add(new CropSet
        {
            Name = setName,
            Day = PsdImport.PathFrom(spec.PanelDir, o.DayPath), DayLayers = o.DayLayers != null && o.Day != null ? [.. o.DayLayers] : null,
            Night = o.NightPath != null ? PsdImport.PathFrom(spec.PanelDir, o.NightPath) : null,
            NightLayers = o.NightPath != null && o.NightLayers != null && o.Night != null ? [.. o.NightLayers] : null,
        });
        return (day, nightPath, setName);
    }

    /// <summary>重ねる画像を、常表示の表示灯にする。</summary>
    private static PanelSection OverlaySection(CropBuildSpec spec, CropOverlay o, OutputNamer namer, CropBuildResult r, int layer)
    {
        var (day, night, set) = OverlayImages(spec, o, namer, r);
        var sec = PanelSection.Create(SectionKind.PilotLamp);
        sec.SetTitle(o.Name.Trim().Length > 0 ? o.Name.Trim() : Path.GetFileNameWithoutExtension(o.DayPath));
        sec.Set("Subject", o.Subject.Trim().Length > 0 ? o.Subject.Trim() : "true");
        sec.Set("Location", PanelValues.Point(o.X, o.Y));
        sec.Set("DaytimeImage", day);
        if (night != null) sec.Set("NighttimeImage", night);
        sec.Set("Layer", Math.Max(0, layer).ToString());
        sec.CropSet = set;
        return sec;
    }
}

/// <summary>書き出すファイルの名前を決める。昼「名前.png」と夜「名前_N.png」の両方がほかとぶつからないようにする。</summary>
public sealed class OutputNamer
{
    private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

    public string Next(string baseName)
    {
        var n = PsdImport.SafeFileName(baseName);
        var name = n;
        for (int i = 2; _used.Contains(name) || _used.Contains(name + "_N"); i++) name = $"{n}_{i}";
        _used.Add(name);
        _used.Add(name + "_N");
        return name;
    }
}

