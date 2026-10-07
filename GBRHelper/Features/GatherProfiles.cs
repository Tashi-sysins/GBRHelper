using System;
using System.Collections.Generic;
using System.Linq;
using GBRHelper.Ipc;

namespace GBRHelper.Features;

public enum GatherProfileKind { Unlock, Stock }

/// <summary>キャラクターごとの選択。名前でなくContentIdで保存し、同名キャラを混同しない。</summary>
public sealed class GatherProfile
{
    public string Name { get; set; } = "";
    public string World { get; set; } = "";
    /// <summary>画面のチェック（キーは GatherProfiles.Key）。チェックしただけでは GBR に書かない。</summary>
    public HashSet<int> Unlock { get; set; } = [];
    public HashSet<int> Stock { get; set; } = [];
    public HashSet<int> Bands(GatherProfileKind kind) => kind == GatherProfileKind.Unlock ? Unlock : Stock;

    /// <summary>
    /// GBR に反映した帯（その機能の「Auto-Gatherに追加」を押した時点のチェック）。要望：
    /// 解放採取と全素材の補充の「Auto-Gatherに追加」は別物。押した機能のリストだけを書き換え、もう一方には触らない。
    /// ログインし直したときも、チェックではなくこちらでリストを作り直す（押していないチェックを勝手に反映しない）。
    /// null は、この仕組みより前の設定（そのころはチェックがそのまま反映されていたので、チェックと同じとみなす。GatherProfiles.FillApplied で埋める）。
    /// </summary>
    public HashSet<int>? AppliedUnlock { get; set; }
    public HashSet<int>? AppliedStock { get; set; }

    /// <summary>GBR に反映した帯（まだ覚えていなければチェックと同じ）。</summary>
    public HashSet<int> Applied(GatherProfileKind kind)
        => (kind == GatherProfileKind.Unlock ? AppliedUnlock : AppliedStock) ?? Bands(kind);

    /// <summary>GBR に反映した帯を置き換える（null で「まだ覚えていない」に戻す）。</summary>
    public void SetApplied(GatherProfileKind kind, HashSet<int>? bands)
    {
        if (kind == GatherProfileKind.Unlock) AppliedUnlock = bands; else AppliedStock = bands;
    }

    /// <summary>反映した帯をまだ覚えていないか（この仕組みより前の設定）。</summary>
    public bool AppliedUnknown(GatherProfileKind kind)
        => (kind == GatherProfileKind.Unlock ? AppliedUnlock : AppliedStock) is null;

    /// <summary>
    /// 全素材の補充の、Lv 帯ごとの「さらに採る数」（キーは GatherProfiles.Key）。無い帯は既定の数。
    /// 要望：999 だと同じ採集場所を長く回り続けて不自然に見えるため、既定 100・帯ごとに変えられるようにした。
    /// 意味は「チェックを入れた時点の所持数に、さらにこの数を足した数になるまで採る」。
    /// </summary>
    public Dictionary<int, int> StockQuantities { get; set; } = new();

    /// <summary>その帯の「さらに採る数」（保存値を範囲に収めて返す。無ければ既定の数）。</summary>
    public int StockQuantity(int key)
        => StockQuantities.TryGetValue(key, out var n) ? GatherProfiles.ClampQuantity(n) : GatherProfiles.DefaultStockQuantity;

    /// <summary>
    /// 全素材の補充で、帯にチェックを入れて最初にリストを作ったときに決めた、品ごとの目標の所持数（その時点の所持数＋さらに採る数）。
    /// キーは GatherProfiles.Key、中身は アイテム番号 → 目標。
    /// リストはログインや再確認のたびに作り直すが、そのたびに「今の所持数＋数」を計算し直すと、採るほど目標が先へ逃げて終わらない。
    /// そのため一度決めた目標はここに残し、作り直しでも同じ目標を使う。チェックを外すと消え、入れ直すとその時点から決め直す。
    /// 「希望所持数」の帯では使わない・覚えない（2026-10-07。目標は「数 − いまのリテイナーの数」で、採っても逃げないので毎回決め直す。
    /// 前は希望所持数の帯もここで覚えていたため、リストを作ったあとリテイナーへ預けても目標が変わらなかった。MaterialPlan.StockBand）。
    /// </summary>
    public Dictionary<int, Dictionary<uint, uint>> StockTargets { get; set; } = new();

    /// <summary>
    /// 全素材の補充で「希望所持数」にチェックを入れた帯（キーは GatherProfiles.Key）。
    /// チェック無し＝チェックした時の手持ちから「さらに N 個」、チェック有り＝「鞄＋リテイナーの合計が N 個になるまで」。
    /// 帯にチェックが入っている間は変えない（数の欄と同じ）。
    /// </summary>
    public HashSet<int> StockDesiredTotal { get; set; } = [];

    /// <summary>
    /// 全素材の補充で、前に「Auto-Gatherに追加」を押してから数・希望所持数を変えた帯（キーは GatherProfiles.Key）。
    /// 次に押したとき、反映した帯のままでもリストを新しい数で作り直す（GatherProfileController.Commit）。押したら空にする。
    /// </summary>
    public HashSet<int> StockSettingsChanged { get; set; } = [];

    /// <summary>
    /// 全素材の補充で「Auto-Gatherに追加」を押した時点の、帯ごとの数（キーは GatherProfiles.Key）。リストの中身と名前（GBRHelper_100_鉱_Lv1-10）はこちらで作る。
    /// 無い帯（この仕組みより前の設定）は、いまの数を使う。
    /// </summary>
    public Dictionary<int, int> AppliedStockQuantities { get; set; } = new();

    /// <summary>押した時点で「希望所持数」にチェックがあった帯（名前は GBRHelper_所持_鉱_Lv1-10）。AppliedStockQuantities に無い帯は、いまのチェックを使う。</summary>
    public HashSet<int> AppliedStockDesired { get; set; } = [];

    /// <summary>押した時点の数（無ければいまの数）。</summary>
    public int AppliedQuantity(int key)
        => AppliedStockQuantities.TryGetValue(key, out var n) ? GatherProfiles.ClampQuantity(n) : StockQuantity(key);

    /// <summary>押した時点で「希望所持数」だったか（無ければいまのチェック）。</summary>
    public bool AppliedDesired(int key)
        => AppliedStockQuantities.ContainsKey(key) ? AppliedStockDesired.Contains(key) : StockDesiredTotal.Contains(key);
}

public static class GatherProfiles
{
    public const string Marker = "[Profiles:1]";

    /// <summary>全素材の補充の「さらに採る数」の既定。</summary>
    public const int DefaultStockQuantity = 100;

    /// <summary>「さらに採る数」の範囲。</summary>
    public const int MinStockQuantity = 1, MaxStockQuantity = 9999;

    /// <summary>GBR へ渡す目標の所持数の上限（解放採取の UnvisitedPlan と同じ。所持数＋数がこれを超えたらここで止める）。</summary>
    public const uint MaxStockTarget = 999_999;

    public static int ClampQuantity(int n) => Math.Clamp(n, MinStockQuantity, MaxStockQuantity);

    /// <summary>
    /// 全素材の補充で、リテイナーの在庫も数えるか。
    /// 要望で、いまは数えない（本人の鞄の所持数だけで判定し、GBR のリストも「リテイナーを数えない」で作る）。
    /// リテイナーの在庫を確かめる仕組み（MaterialInventory.Read の retainers・StockFeature の見張り）は「後で使う」とのことで残してある。
    /// 戻すときはここを true にするだけでよい（判定・リストの設定・見張りがすべてこの値に従う）。
    /// static readonly にしているのは、const にすると使わない側の分岐が「到達しないコード」の警告になるため。
    /// </summary>
    public static readonly bool StockUsesRetainers = false;
    public static int Key(GatherableCatalog.Job job, GatherableCatalog.LevelBand band) => (int)job * 10 + band.SegmentIndex;
    public static (GatherableCatalog.Job Job, GatherableCatalog.LevelBand Band) Decode(int key)
    {
        if (key is < 0 or >= 20) throw new ArgumentOutOfRangeException(nameof(key));
        return ((GatherableCatalog.Job)(key / 10), new(key % 10));
    }
    /// <summary>
    /// GBR のリストにする帯（そのキャラクターの、その機能で「Auto-Gatherに追加」を押して反映した帯。GatherProfile.Applied）。
    /// 画面のチェック（GatherProfile.Bands）ではない（2026-10-05：チェックだけでは GBR に書かない）。写しを返す。
    /// </summary>
    public static HashSet<int> Effective(IReadOnlyDictionary<ulong, GatherProfile> profiles, ulong character, GatherProfileKind kind)
    {
        if (character == 0) return [];
        var result = new HashSet<int>();
        if (profiles.TryGetValue(character, out var personal)) result.UnionWith(personal.Applied(kind));
        result.RemoveWhere(k => k is < 0 or >= 20);
        return result;
    }

    /// <summary>
    /// この仕組みより前の設定で、反映した帯をまだ覚えていないものを、そのときのチェックで埋める（そのころはチェックがそのまま反映されていた）。
    /// 変えたら true（呼んだ側が保存する）。
    /// </summary>
    public static bool FillApplied(IEnumerable<GatherProfile> profiles)
    {
        var changed = false;
        foreach (var profile in profiles)
            foreach (var kind in Enum.GetValues<GatherProfileKind>())
                if (profile.AppliedUnknown(kind))
                {
                    profile.SetApplied(kind, new HashSet<int>(profile.Bands(kind)));
                    changed = true;
                }
        return changed;
    }
    /// <summary>
    /// GBR に作るリストの名前（利用者が GBR の Auto-Gather で見る名前。指定で短くした）。
    /// 解放採取：GBRHelper_開放_園_Lv1-10_未採取（前の版は「解放」。UnvisitedFeature.ListNamePrefix の説明）。
    /// 全素材の補充：GBRHelper_100_鉱_Lv1-10（さらに 100 個）／GBRHelper_所持_鉱_Lv11-20（希望所持数）。
    /// キャラクターの番号は名前に入れない（説明欄の印 Tag に入っているので、それで自分のリストか・誰のリストかを見分ける）。
    /// 設定の記録（GatherListMemory など）の鍵には、いままでどおり Name を使う（名前が数で変わっても記録が切れないように）。
    /// </summary>
    public static string ListName(GatherProfileKind kind, int key, GatherProfile? profile)
    {
        var (job, band) = Decode(key);
        if (kind == GatherProfileKind.Unlock)
            return UnvisitedPlan.FormatListName(UnvisitedFeature.ListNamePrefix, job, band);
        var mode = profile is not null && profile.AppliedDesired(key)
            ? DesiredLabel
            : (profile?.AppliedQuantity(key) ?? DefaultStockQuantity).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"{StockNamePrefix}_{mode}_{JobLabel(job)}_{band.Label}";
    }

    /// <summary>全素材の補充のリスト名の頭と、「希望所持数」の帯の印。</summary>
    private const string StockNamePrefix = "GBRHelper";
    private const string DesiredLabel = "所持";

    private static string JobLabel(GatherableCatalog.Job job) => job == GatherableCatalog.Job.Miner ? "鉱" : "園";

    /// <summary>この形の名前が、その機能・帯の GBR のリスト名か（全素材の補充は数の所が変わるので形で見る）。</summary>
    private static bool IsListName(GatherProfileKind kind, int key, string name)
    {
        var (job, band) = Decode(key);
        // 解放採取：いまの「開放」の名前と、前の版の「解放」の名前（キャラクターの番号なし）。
        if (kind == GatherProfileKind.Unlock)
            return name == ListName(kind, key, null) || name == UnvisitedPlan.FormatListName(UnvisitedFeature.ManagementTagPrefix, job, band);
        var parts = name.Split('_');
        return parts.Length == 4 && parts[0] == StockNamePrefix && parts[2] == JobLabel(job) && parts[3] == band.Label
            && (parts[1] == DesiredLabel || (parts[1].Length is > 0 and <= 6 && parts[1].All(char.IsAsciiDigit)));
    }

    public static string BaseTag(GatherProfileKind kind)
        => kind == GatherProfileKind.Unlock ? UnvisitedFeature.ManagementTag : "[GBRHelper:Stock999]";
    public static string Tag(GatherProfileKind kind, ulong character) => $"{BaseTag(kind)}{Marker}[Character:{character}]";
    public static string Prefix(ulong character) => $"{UnvisitedFeature.ManagementTagPrefix}_C{character}";
    /// <summary>
    /// 設定の記録（GatherListMemory・GatherListReset・GatherListAddedDisabled）の鍵。キャラクターの番号入り。
    /// 2026-10-05 夕方までは GBR のリスト名もこれだった（いまは ListName。前の版で作ったこの名前のリストも自分のリストとして見分ける）。
    /// </summary>
    public static string Name(GatherProfileKind kind, int key, ulong character)
    {
        var (job, band) = Decode(key);
        return kind == GatherProfileKind.Unlock ? UnvisitedPlan.FormatListName(Prefix(character), job, band)
            : $"GBRHelper_999_{(job == GatherableCatalog.Job.Miner ? "鉱" : "園")}_{band.Label}_C{character}";
    }
    public sealed record Owned(string Name, string Tag, GatherProfileKind Kind, ulong Character, int Key, bool Legacy);
    public static Owned? Identify(GbrAutoGatherListAccess.ListSummary list)
    {
        foreach (var kind in Enum.GetValues<GatherProfileKind>())
        {
            var prefix = BaseTag(kind);
            if (!list.Description.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var tail = list.Description[prefix.Length..];
            bool legacy = !tail.StartsWith(Marker, StringComparison.Ordinal);
            if (!legacy) tail = tail[Marker.Length..];
            ulong cid = 0;
            if (tail.Length != 0)
            {
                if (!tail.StartsWith("[Character:", StringComparison.Ordinal) || !tail.EndsWith(']') ||
                    !ulong.TryParse(tail[11..^1], out cid) || cid == 0) continue;
            }
            else if (!legacy || kind != GatherProfileKind.Unlock) continue;
            for (var key = 0; key < 20; key++)
            {
                var (job, band) = Decode(key);
                if (legacy)
                {
                    var expected = kind == GatherProfileKind.Unlock ? UnvisitedPlan.FormatListName(UnvisitedFeature.ManagementTagPrefix, job, band)
                        : $"GBRHelper_999_{(job == GatherableCatalog.Job.Miner ? "鉱" : "園")}_{band.Label}";
                    if (list.Name == expected) return new(list.Name, list.Description, kind, cid, key, legacy);
                }
                // いまの名前（ListName）か、前の版の名前（キャラクターの番号入りの Name）。誰のリストかは説明欄の印で決まる。
                else if (IsListName(kind, key, list.Name) || list.Name == Name(kind, key, cid))
                    return new(list.Name, list.Description, kind, cid, key, legacy);
            }
        }
        return null;
    }
}
