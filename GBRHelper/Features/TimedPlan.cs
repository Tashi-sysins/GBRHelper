using System;
using System.Collections.Generic;
using System.Linq;
using GBRHelper.Ipc;

namespace GBRHelper.Features;

/// <summary>
/// 霊砂の目標から、GBR の自動採集リストに載せる原料（精選すると霊砂になる収集品）を決める。ゲームに触らない純粋な部品。
///
/// 【決まりごと】
///   ・原料の数は 1 品 200 個。収集品は1個1枠で鞄に 200 個は入らないので、実際には GBR と本プラグインが精選して
///     鞄の中の原料を減らしながら採り続ける（GBR の「数に達したら止まる」で止まらないための数）。
///   ・霊砂が目標数に達したら、その霊砂の原料をリストから外す（GBR は霊砂の数を見ないので、本プラグインが見て外す）。
///     ほかの、まだ足りない霊砂の原料でもある品は残す。
///   ・並びは、渡された霊砂の順（画面の上から）→ その霊砂の原料をレベルの低い順。
///     GBR の並べ替え（Item Sorting Method）を None にすると、GBR はリストの上から「いま採れる品」を採りに行く。
/// </summary>
public static class TimedPlan
{
    /// <summary>リストに載せる原料 1 品あたりの数。</summary>
    public const uint SourceQuantity = 200;

    public sealed record Goal(uint ItemId, uint Target);

    /// <param name="catalog">採れる品の一覧（原料のレベルを引く）。</param>
    /// <param name="sands">欲しい霊砂と目標数。この順にリストへ並べる。</param>
    /// <param name="recipes">霊砂と原料の対応（採れる原料だけ）。</param>
    /// <param name="held">霊砂の所持数（本人の鞄）。</param>
    /// <param name="sourcesHeld">原料の所持数（通常品と収集品を分けて）。通常品を持っていたら登録しない。</param>
    public static List<(uint ItemId, uint Quantity)> Build(IReadOnlyList<GatherableCatalog.Entry> catalog,
        IReadOnlyList<Goal> sands, IReadOnlyList<AethersandRecipe> recipes,
        IReadOnlyDictionary<uint, int> held, IReadOnlyDictionary<uint, ReductionStock> sourcesHeld)
    {
        if (sands.Any(g => g.Target is < 1 or > 9999 || !held.TryGetValue(g.ItemId, out var n) || n < 0))
            throw new InvalidOperationException("目標数または現在の所持数を確認できません");
        ReductionInventory.RequireNormalFree(sourcesHeld, recipes.Select(r => r.SourceId),
            id => catalog.First(e => e.ItemId == id).Name);

        var rows = new List<(uint ItemId, uint Quantity)>();
        var added = new HashSet<uint>();
        foreach (var g in sands)
        {
            var sources = recipes.Where(r => r.OutputId == g.ItemId).Select(r => r.SourceId).Distinct().ToArray();
            if (sources.Length == 0)
                throw new InvalidOperationException("選んだ霊砂の採集可能な原料がありません");
            if (held[g.ItemId] >= g.Target)
                continue;

            foreach (var source in sources.OrderBy(id => catalog.First(e => e.ItemId == id).Level).ThenBy(id => id))
                if (added.Add(source))
                    rows.Add((source, SourceQuantity));
        }

        return rows;
    }

    /// <summary>
    /// まだ目標に届いていない霊砂の原料（リストで有効にしておく品）。届いた霊砂だけの原料は入らない（無効にする）。
    /// 要望：霊砂が目標に届いたら、その霊砂の原料をリストから消すのではなく無効にする（利用者が手で有効に戻せるように）。
    /// </summary>
    public static HashSet<uint> NeededSources(IReadOnlyList<Goal> sands, IReadOnlyList<AethersandRecipe> recipes, IReadOnlyDictionary<uint, int> held)
    {
        if (sands.Any(g => !held.TryGetValue(g.ItemId, out var n) || n < 0))
            throw new InvalidOperationException("霊砂の所持数を確認できません");
        return sands.Where(g => held[g.ItemId] < g.Target)
            .SelectMany(g => recipes.Where(r => r.OutputId == g.ItemId).Select(r => r.SourceId))
            .ToHashSet();
    }

    /// <summary>
    /// 画面に並べる霊砂の順。原料の一番低いレベルの順（同じなら品の番号順）。
    /// 原料が一覧に無い霊砂は最後に回す。
    /// </summary>
    public static List<uint> SandOrder(IReadOnlyList<AethersandRecipe> recipes, Func<uint, int?> sourceLevel)
        => recipes.GroupBy(r => r.OutputId)
            .Select(g => (Id: g.Key, Level: g.Select(r => sourceLevel(r.SourceId)).Where(l => l is not null).Min() ?? int.MaxValue))
            .OrderBy(x => x.Level).ThenBy(x => x.Id)
            .Select(x => x.Id).ToList();
}
