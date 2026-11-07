using System.Text.RegularExpressions;
using BvePanelEditor.Core.Psd;

namespace BvePanelEditor.Core.Crop;

/// <summary>4 点の画像の 1 つ（昼・無発光など）が、どの時間帯・発光か。</summary>
public readonly record struct CropSlotKind(bool Night, bool Lit);

/// <summary>PSD のレイヤーを選んで重ね、4 点の画像の 1 つにする。</summary>
public static partial class CropLayers
{
    /// <summary>
    /// 選んだレイヤー（パス。「#2」まで含めて完全一致）をキャンバス大に重ねる。フォルダを選んだら中の画像レイヤー全部。
    /// 表示・非表示は見ない。見つからないレイヤーは missing に入れる。
    /// </summary>
    public static PsdPixels Compose(PsdFile psd, IEnumerable<string> layers, ICollection<string>? warnings = null, ICollection<string>? missing = null)
    {
        var picked = new List<PsdLayer>();
        foreach (var p in layers)
        {
            var l = psd.Layers.FirstOrDefault(x => x.Kind != PsdLayerKind.FolderEnd && x.Path == p);
            if (l == null) { missing?.Add(p); continue; }
            if (l.IsFolder) picked.AddRange(psd.ComposableLayers.Where(x => IsInside(x, l)));
            else picked.Add(l);
        }
        var w = new List<string>();
        var img = PsdCompose.Compose(psd, picked.Distinct(), psd.Canvas, 1, w);
        if (warnings != null)
        {
            foreach (var x in w.Distinct()) warnings.Add(x);
        }
        return img;
    }

    private static bool IsInside(PsdLayer l, PsdLayer folder)
    {
        for (var p = l.Parent; p != null; p = p.Parent) if (p == folder) return true;
        return false;
    }

    [GeneratedRegex(@"(?i)光|点灯|(^|[^a-z])(tt|on|lit|glow)($|[^a-z])")]
    private static partial Regex LitRegex();

    [GeneratedRegex(@"(?i)夜|night|(^|[^a-z])n($|[^a-z])|(?-i:[a-z0-9]N)$")]
    private static partial Regex NightRegex();

    /// <summary>名前（フォルダ名を含むパス）が発光らしいか。例: 「発光」「光」「点灯」「ATS_TT」「lamp on」。</summary>
    public static bool IsLitName(string path) => LitRegex().IsMatch(PsdFile.StripDuplicateMark(path));

    /// <summary>名前（フォルダ名を含むパス）が夜らしいか。例: 「夜」「night」「panel_N」「carN」。</summary>
    public static bool IsNightName(string path) => NightRegex().IsMatch(PsdFile.StripDuplicateMark(path));

    [GeneratedRegex(@"(?i)night|夜")]
    private static partial Regex NightWordRegex();

    /// <summary>
    /// 昼と夜の対を見つけるための名前: 「night」「夜」を外し、まわりの区切り（空白・_・-）とフォルダの区切りを整えて小文字にする。
    /// 例: 「glow night」「Night/glow」「glow_夜」はどれも「glow」。夜の語がなければ null。
    /// </summary>
    public static string? NightPairKey(string path)
    {
        var p = PsdFile.StripDuplicateMark(path);
        if (!NightWordRegex().IsMatch(p)) return null;
        return PairKey(NightWordRegex().Replace(p, ""));
    }

    // 区切り（空白・_・-）は続けても 1 つとみなす（「car_Night_glow」から Night を外した「car__glow」も「car_glow」と対）
    private static string PairKey(string path) =>
        string.Join("/", path.Split('/').Select(s => SeparatorRegex().Replace(s, "_").Trim('_')).Where(s => s.Length > 0)).ToLowerInvariant();

    [GeneratedRegex(@"[ _\-　]+")]
    private static partial Regex SeparatorRegex();

    /// <summary>
    /// 1 つの PSD を slots の欄に使うときの、欄ごとのレイヤーの初期値（画像レイヤーのパス、下から順）。
    /// 発光と無発光の両方に使う PSD なら名前で発光のレイヤーを分け、昼と夜の両方に使う PSD なら名前で夜のレイヤーを分ける。
    /// どちらでもなければ、表示されているレイヤーをそのまま使う。空の欄は「この PSD からは作れない」。
    /// 発光のレイヤーが絵全体の大きさなら（発光した絵をまるごと入れてある PSD）、発光の欄はそのレイヤーだけにする。
    /// </summary>
    public static List<string>[] Guess(PsdFile psd, IReadOnlyList<CropSlotKind> slots)
    {
        bool splitLit = slots.Any(s => s.Lit) && slots.Any(s => !s.Lit);
        bool splitNight = slots.Any(s => s.Night) && slots.Any(s => !s.Night);
        var layers = psd.ComposableLayers.ToList();
        bool anyLit = splitLit && layers.Any(l => IsLitName(l.Path));
        bool anyNightLit = splitNight && layers.Any(l => IsLitName(l.Path) && IsNightName(l.Path));
        bool anyNight = splitNight && layers.Any(l => IsNightName(l.Path));
        // 夜らしいレイヤーのうち表示されているものがあれば、隠れている夜の案（別の仕様など）は使わない
        bool anyVisibleNight = splitNight && layers.Any(l => IsNightName(l.Path) && !IsLitName(l.Path) && !l.EffectivelyHidden);
        // 「X night」と対になる「X」は昼だけのレイヤー（夜の欄には入れない）。対のないレイヤーは昼と夜の両方に使う
        var nightPairs = splitNight
            ? layers.Select(l => NightPairKey(l.Path)).OfType<string>().ToHashSet()
            : [];
        bool DayOnly(PsdLayer l) => nightPairs.Count > 0 && !IsNightName(l.Path) && nightPairs.Contains(PairKey(PsdFile.StripDuplicateMark(l.Path)));

        var result = new List<string>[slots.Count];
        for (int k = 0; k < slots.Count; k++)
        {
            var slot = slots[k];
            // 昼と夜の両方に使う PSD でも、夜らしいレイヤーがなければ夜の欄は空（別の画像を選んでもらう）
            if (splitNight && slot.Night && !anyNight) { result[k] = []; continue; }
            var picked = layers.Where(Include).OrderBy(l => l.Index).ToList();
            // 発光のレイヤーが絵全体の半分以上なら、上に重ねる灯ではなく発光した絵そのもの（下に隠れないよう、それだけを使う）
            if (slot.Lit && splitLit)
            {
                var lit = picked.Where(l => IsLitName(l.Path)).ToList();
                long canvasArea = (long)psd.Width * psd.Height;
                if (lit.Any(l => { var r = l.Bounds.Intersect(psd.Canvas); return (long)r.Width * r.Height * 2 >= canvasArea; })) picked = lit;
            }
            result[k] = picked.Select(l => l.Path).ToList();

            bool Include(PsdLayer l)
            {
                bool n = splitNight && IsNightName(l.Path);
                bool t = splitLit && IsLitName(l.Path);
                if (n && !slot.Night) return false;
                if (!n && slot.Night && DayOnly(l)) return false;
                if (t)
                {
                    if (!slot.Lit) return false;
                    // 夜の発光は、夜らしい発光のレイヤーがあればそちらだけ
                    if (splitNight && slot.Night && !n && anyNightLit) return false;
                    return true;
                }
                if (n) return !anyVisibleNight || !l.EffectivelyHidden;
                if (!l.EffectivelyHidden) return true;
                // 発光らしい名前がない PSD では、隠れている小さいレイヤーを表示灯とみなす
                if (slot.Lit && splitLit && !anyLit && PsdImport.DefaultRole(l, psd.Canvas) == PsdRole.Lamp) return true;
                return false;
            }
        }
        return result;
    }
}
