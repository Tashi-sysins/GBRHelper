using System;
using System.Collections.Generic;
using System.Linq;

namespace GBRHelper.Features;

[Flags]
public enum MaterialNodeKinds { None = 0, Regular = 1, Once = 2, Repeat = 4, Other = 8 }

/// <summary>所持数の差分は表示用。GBRには必ず目標の総数を渡す。</summary>
public static class MaterialPlan
{
    public sealed record Row(uint ItemId, string Name, uint Target, int Held)
    {
        public long Missing => Math.Max(0L, (long)Target - Held);
    }

    public static bool CanStock(GatherableCatalog.Entry e, int level, Func<GatherableCatalog.Entry, bool?> folklore)
        => !e.Collectable && !e.TreasureMap && !IsRestoration(e) && e.Level <= level &&
            e.NodeKinds != MaterialNodeKinds.None && folklore(e) == true;

    /// <summary>
    /// イシュガルド復興用の採取品か（全素材の補充から除く。要望「復興用の○○…の採取物は除外」）。
    /// ゲームデータの確認（2026-10-05）：採取品のうち名前に「復興用」が入るのは 179 件で、すべて説明文に「イシュガルド復興用資材」等の
    /// 「復興」が入り、並べ替えの区分（ItemSortCategory）もこの 179 件だけで 1 つにまとまる（ほかの採取品は 0 件）。
    /// 区分の番号を埋め込まず、霊砂（GbrTimedAccess.LoadRecipes）と同じく名前で見分ける。英語版は「Skybuilders'」。
    /// </summary>
    public static bool IsRestoration(GatherableCatalog.Entry e)
        => e.Name.Contains("復興用", StringComparison.Ordinal) || e.Name.StartsWith("Skybuilders'", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 全素材の補充で登録する行。「チェックを入れた時点の所持数に、さらに add 個を足した数」を目標にする。
    /// GBR の個数は「所持数がこの数になるまで採る」なので、GBR へは差分ではなく目標の所持数を渡す。
    /// fixedTargets に目標が決まっている品はそれを使う（作り直しのたびに目標が先へ逃げないため。GatherProfile.StockTargets）。
    /// 決まっていない品は「今の所持数＋add」（上限 MaxStockTarget）。目標に届いた品は登録しない。
    /// 【希望所持数】desiredRetainers を渡すと（その帯の「希望所持数」にチェック）、
    ///   「鞄＋リテイナーの合計が add 個になるまで」にする。GBR は鞄だけで数える（リストの UsesRetainerInventory は false）ので、
    ///   GBR へは「add − リテイナーの数」を目標として渡す（鞄がその数になったとき、鞄＋リテイナー＝add）。
    /// </summary>
    public static List<Row> Stock(IEnumerable<GatherableCatalog.Entry> entries, int level,
        Func<GatherableCatalog.Entry, bool?> folklore, IReadOnlyDictionary<uint, int> counts,
        IReadOnlySet<uint> conflicts, int add, IReadOnlyDictionary<uint, uint>? fixedTargets = null,
        IReadOnlyDictionary<uint, int>? desiredRetainers = null)
    {
        var amount = GatherProfiles.ClampQuantity(add);
        return entries.Where(e => CanStock(e, level, folklore) && !conflicts.Contains(e.ItemId))
            .DistinctBy(e => e.ItemId)
            .Where(e => counts.TryGetValue(e.ItemId, out var n) && n >= 0)
            .Select(e =>
            {
                var held = counts[e.ItemId];
                var target = fixedTargets is not null && fixedTargets.TryGetValue(e.ItemId, out var t)
                    ? t
                    : desiredRetainers is not null
                        ? (uint)Math.Max(0L, (long)amount - desiredRetainers.GetValueOrDefault(e.ItemId))
                        : (uint)Math.Min((long)held + amount, GatherProfiles.MaxStockTarget);
                return new Row(e.ItemId, e.Name, target, held);
            })
            .Where(r => r.Held < r.Target)
            .ToList();
    }

    /// <summary>
    /// 全素材の補充の画面に出す、帯の「採る候補の品」（目標を決める前）。FixedTarget は決めてある目標（GatherProfile.StockTargets）。
    /// 画面で指定した数量から目標を計算するため、候補と目標を分ける。
    /// </summary>
    public sealed record Candidate(uint ItemId, string Name, int Held, uint? FixedTarget);

    /// <summary>
    /// Stock と同じ選び方で候補の品を出す（目標を決めてある品は、目標に届いていれば除く）。
    /// 数（add）と希望所持数は、あとから FromCandidates で決める（数で候補は変わらない：決めていない品は「今の所持数＋1 以上」が目標なので必ず残る）。
    /// </summary>
    public static List<Candidate> Candidates(IEnumerable<GatherableCatalog.Entry> entries, int level,
        Func<GatherableCatalog.Entry, bool?> folklore, IReadOnlyDictionary<uint, int> counts,
        IReadOnlySet<uint> conflicts, IReadOnlyDictionary<uint, uint>? fixedTargets = null)
        => Stock(entries, level, folklore, counts, conflicts, 1, fixedTargets)
            .Select(r => new Candidate(r.ItemId, r.Name, r.Held,
                fixedTargets is not null && fixedTargets.TryGetValue(r.ItemId, out var t) ? t : null))
            .ToList();

    /// <summary>候補の品から、Stock と同じ目標の行を出す（add・desiredRetainers の意味は Stock と同じ）。</summary>
    public static List<Row> FromCandidates(IEnumerable<Candidate> candidates, int add,
        IReadOnlyDictionary<uint, int>? desiredRetainers = null)
    {
        var amount = GatherProfiles.ClampQuantity(add);
        return candidates
            .Select(c => new Row(c.ItemId, c.Name,
                c.FixedTarget ?? (desiredRetainers is not null
                    ? (uint)Math.Max(0L, (long)amount - desiredRetainers.GetValueOrDefault(c.ItemId))
                    : (uint)Math.Min((long)c.Held + amount, GatherProfiles.MaxStockTarget)),
                c.Held))
            .Where(r => r.Held < r.Target)
            .ToList();
    }

    public static bool IsTimed(GatherableCatalog.Entry e)
        => (e.NodeKinds & (MaterialNodeKinds.Once | MaterialNodeKinds.Repeat)) != 0
            && (e.NodeKinds & (MaterialNodeKinds.Regular | MaterialNodeKinds.Other)) == 0;

    // 未知・伝説を先に並べる順（Priority）は、霊砂の機能から時限素材を外したとき（2026-10-05）に使われなくなったので外した。
}
