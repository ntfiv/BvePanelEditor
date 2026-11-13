using BvePanelEditor.Core.Crop;
using BvePanelEditor.Core.Psd;

namespace BvePanelEditor.Core;

/// <summary>
/// メインパネル: パネルの土台を [This] ではなく、常表示の表示灯にする形。
/// [This] には画像を書かない（BVE は [This] の画像がなくても読める。メインパネルより下に窓などを置けるように）。
/// </summary>
public static class MainPanel
{
    public const string Title = "メインパネル";
    public const int Layer = 1;

    /// <summary>前の版のパネルくんが [This] に書いていた透明な画像（this_blank_幅x高さ.png）か。</summary>
    public static bool IsLegacyBlank(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.GetFileName(path.Trim().Trim('"')).StartsWith("this_blank_", StringComparison.OrdinalIgnoreCase);

    /// <summary>[This] に表示に使う画像がないか（画像なし、または前の版の透明な画像）。</summary>
    public static bool ThisHasNoImage(PanelDocument doc) =>
        doc.This is { } t && (string.IsNullOrWhiteSpace(t.Get("DaytimeImage")) || IsLegacyBlank(t.Get("DaytimeImage")));

    /// <summary>メインパネルの表示灯（常表示・0, 0）。</summary>
    public static PanelSection Lamp(string day, string? night, int layer = Layer)
    {
        var s = PanelSection.Create(SectionKind.PilotLamp);
        s.SetTitle(Title);
        s.Set("Subject", "true");
        s.Set("Location", "0, 0");
        s.Set("DaytimeImage", day);
        if (!string.IsNullOrWhiteSpace(night)) s.Set("NighttimeImage", night);
        s.Set("Layer", layer.ToString());
        s.IsMainPanel = true;
        return s;
    }

    /// <summary>新しいパネル: 画像のない [This] と、メインパネルの表示灯。</summary>
    public static PanelDocument CreateNew(string mainDay, string? mainNight, int w, int h, string header = PanelDocument.DefaultHeader)
    {
        var doc = PanelDocument.CreateNew(null, w, h, header);
        doc.Sections.Add(Lamp(mainDay, mainNight));
        return doc;
    }

    private static int LayerOf(PanelSection s) => (int)Math.Round(PanelValues.Double(s.Get("Layer"), 0));

    /// <summary>ずらす量を決めるときに数えるオブジェクト（Layer を書いてあるもの。中身のないオブジェクトなどは数えない）。</summary>
    private static bool HasLayer(PanelSection s) => !string.IsNullOrWhiteSpace(s.Get("Layer"));

    /// <summary>
    /// keep より下（レイヤーが floor 以下）に、ほかのオブジェクトがあれば、それらを全部同じだけ上げて floor より上にする。
    /// [This] と keep は動かさない。上げた量を返す。
    /// </summary>
    public static int RaiseAbove(PanelDocument doc, int floor, params PanelSection[] keep)
    {
        var others = doc.Sections.Where(s => s.Kind is SectionKind.PilotLamp or SectionKind.Needle or SectionKind.DigitalNumber or SectionKind.DigitalGauge
            && !keep.Contains(s)).ToList();
        if (!others.Any(HasLayer)) return 0;
        int min = others.Where(HasLayer).Min(LayerOf);
        if (min > floor) return 0;
        int d = floor + 1 - min;
        foreach (var s in others) s.Set("Layer", (LayerOf(s) + d).ToString());
        return d;
    }

    /// <summary>[This] が持っていた切り抜きの組「本体」を、表示灯が使う名前に変える（「切り抜き元から更新」で透明な [This] を上書きしないように）。</summary>
    private static string? MoveBaseSet(PanelDocument doc)
    {
        var set = doc.CropSets.FirstOrDefault(c => c.Name == CropSetNames.Base);
        if (set == null) return null;
        var name = CropSetNames.PanelBody;
        for (int i = 2; doc.CropSets.Any(c => c.Name == name); i++) name = $"{CropSetNames.PanelBody} {i}";
        set.Name = name;
        return name;
    }

    /// <summary>[This] の画像を、表示灯（PSD・切り抜きのつながりごと）に移す。[This] からは画像を外す。</summary>
    private static PanelSection? TakeThisImage(PanelDocument doc, string title, int layer, bool main)
    {
        if (doc.This is not { } self || self.Get("DaytimeImage") is not { Length: > 0 } day || IsLegacyBlank(day)) return null;
        var lamp = Lamp(day, self.Get("NighttimeImage"), layer);
        lamp.IsMainPanel = main;
        lamp.SetTitle(title);
        lamp.PsdLayers.AddRange(self.PsdLayers);
        lamp.PsdSource = self.PsdSource;
        self.PsdLayers.Clear();
        lamp.CropSet = MoveBaseSet(doc);
        // 透過色も画像と一緒に移す（画像のない [This] には要らない）
        if (self.Get("TransparentColor") is { Length: > 0 } tc) { lamp.Set("TransparentColor", tc); self.Set("TransparentColor", null); }
        self.Set("DaytimeImage", null);
        self.Set("NighttimeImage", null);
        // ファイルでは [This] のすぐ後ろに置く
        doc.Sections.Insert(doc.Sections.IndexOf(self) + 1, lamp);
        return lamp;
    }

    /// <summary>
    /// 今の [This] の画像をメインパネル（レイヤー 1）にする。ほかのオブジェクトがレイヤー 1 以下なら全部を上げる。
    /// [This] からは画像を外す。メインパネルの番号を返す（[This] に画像がなければ -1）。
    /// </summary>
    public static int FromThis(PanelDocument doc)
    {
        foreach (var s in doc.Sections) s.IsMainPanel = false;
        var lamp = TakeThisImage(doc, Title, Layer, main: true);
        if (lamp == null) return -1;
        RaiseAbove(doc, Layer, lamp);
        return doc.Sections.IndexOf(lamp);
    }

    /// <summary>
    /// 今ある表示灯をメインパネルにする。moveThisImage で [This] に画像があれば、その画像を常表示の表示灯に移し
    /// （いちばん下。レイヤー 0 未満になるならほかを上げる）、[This] から画像を外す。移した表示灯の番号を返す（移さなければ -1）。
    /// </summary>
    public static int MakeMain(PanelDocument doc, int index, bool moveThisImage)
    {
        foreach (var s in doc.Sections) s.IsMainPanel = false;
        var main = doc.Sections[index];
        if (main.Kind != SectionKind.PilotLamp) throw new ArgumentException("メインパネルにできるのは表示灯だけです");
        main.IsMainPanel = true;
        if (!moveThisImage || doc.This?.Get("DaytimeImage") is not { Length: > 0 } day || IsLegacyBlank(day)) return -1;
        var others = doc.Sections.Where(s => s.Kind is SectionKind.PilotLamp or SectionKind.Needle or SectionKind.DigitalNumber or SectionKind.DigitalGauge).ToList();
        int bottom = others.Any(HasLayer) ? others.Where(HasLayer).Min(LayerOf) - 1 : 0;
        if (bottom < 0)
        {
            foreach (var s in others) s.Set("Layer", (LayerOf(s) - bottom).ToString());
            bottom = 0;
        }
        var moved = TakeThisImage(doc, Path.GetFileNameWithoutExtension(day.Trim().Trim('"')), bottom, main: false);
        return moved != null ? doc.Sections.IndexOf(moved) : -1;
    }
}
