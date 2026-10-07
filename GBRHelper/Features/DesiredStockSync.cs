using System;
using System.Collections.Generic;
using System.Linq;

namespace GBRHelper.Features;

/// <summary>
/// 全素材の補充の「希望所持数」の帯で、GBR のリストを作った時点の 1 品ぶんの数。
/// Retainer＝リテイナーの数（GBR へは「希望所持数 − これ」を渡した）、Needed＝鞄＋リテイナーが希望所持数に足りなかった（リストに入れる品だった）。
/// </summary>
public sealed record DesiredItemBasis(int Retainer, bool Needed);

/// <summary>
/// 「希望所持数」の帯の GBR のリストを、いまのリテイナーの数に合わせて作り直すかの判断（2026-10-07）。
///
/// 【なぜ要るか】GBR は鞄の数だけで目標と比べる（GBR の全体設定「Check Retainer Inventories」が OFF のとき。
/// GatherableExtensions.cs 46-50）。だからリストには「希望所持数 − リテイナーの数」を入れているが、
/// その後リテイナーへ預けたり引き出したりすると、リストの数が実際とずれる。前はリストを作ったときの数を品ごとに覚えて
/// 使い回していたので（GatherProfile.StockTargets）、リテイナーに 301 個あっても「300 個まで採る」リストのままだった
/// （指摘：黄鉄鉱 鞄 0＋リテイナー 301 なのに GBR のリストは 0/300）。
///
/// 【作り直す】・リテイナーの数が変わった品がある（預けた・引き出した・ベンチャーの戦利品・AutoRetainer の預け入れ）
///             ・作ったときは足りていた品が、いま足りない（鞄から減った。リストに入っていないので、作り直さないと採らない）
/// 【作り直さない】採って足りた品（GBR が自分で飛ばす。GBR を止めるたびに書き直さない）・新しく候補になった品
///             （レベル・伝承録。全素材の補充は「Auto-Gatherに追加」を押したときに入れる作りのまま）。
/// 作ったときの数が無い（ログインし直した・プラグインを読み直した）ときは、一度作り直して確かめる。
/// </summary>
public static class DesiredStockSync
{
    /// <summary>
    /// 品ごとの数を測る。bag は鞄の数（読めた品だけ）、retainers はリテイナーの数（無い品は 0 個）。
    /// Needed の決め方は MaterialPlan.Stock と同じ（鞄 &lt; 希望所持数 − リテイナー ⇔ 鞄＋リテイナー &lt; 希望所持数）。
    /// </summary>
    public static Dictionary<uint, DesiredItemBasis> Measure(IEnumerable<uint> itemIds, IReadOnlyDictionary<uint, int> bag,
        IReadOnlyDictionary<uint, int> retainers, int desired)
    {
        var amount = GatherProfiles.ClampQuantity(desired);
        var result = new Dictionary<uint, DesiredItemBasis>();
        foreach (var id in itemIds)
        {
            if (result.ContainsKey(id) || !bag.TryGetValue(id, out var held) || held < 0) continue;
            var retainer = retainers.GetValueOrDefault(id);
            result[id] = new(retainer, (long)held + retainer < amount);
        }
        return result;
    }

    /// <summary>作り直す理由。</summary>
    public enum Reason { NoBasis, RetainerChanged, Shortage }

    /// <summary>作り直すきっかけ（記録に残す。ItemId・Before・After は NoBasis では使わない。Before・After はリテイナーの数）。</summary>
    public sealed record Change(Reason Reason, uint ItemId = 0, int Before = 0, int After = 0);

    /// <summary>作り直すきっかけになった最初の品（作り直さなくてよければ null）。</summary>
    public static Change? Find(IReadOnlyDictionary<uint, DesiredItemBasis>? basis, IReadOnlyDictionary<uint, DesiredItemBasis> now)
    {
        if (basis is null) return new(Reason.NoBasis);
        foreach (var (id, current) in now.OrderBy(p => p.Key))
        {
            if (!basis.TryGetValue(id, out var old)) continue;
            if (old.Retainer != current.Retainer) return new(Reason.RetainerChanged, id, old.Retainer, current.Retainer);
            if (!old.Needed && current.Needed) return new(Reason.Shortage, id, old.Retainer, current.Retainer);
        }
        return null;
    }
}
