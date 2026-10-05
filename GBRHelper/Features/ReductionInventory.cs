using System;
using System.Collections.Generic;
using System.Linq;

namespace GBRHelper.Features;

/// <summary>同じItemIdでも、通常品は精選できない。合計を精選の判定に使わない。</summary>
public sealed record ReductionStock(int Normal, int Collectable);

public static class ReductionInventory
{
    public sealed record Slot(uint ItemId, int Quantity, bool IsCollectable);

    public static Dictionary<uint, ReductionStock> Count(IEnumerable<Slot> slots, IReadOnlySet<uint> sources)
    {
        var counts = sources.ToDictionary(id => id, _ => new ReductionStock(0, 0));
        foreach (var slot in slots.Where(s => sources.Contains(s.ItemId)))
        {
            if (slot.Quantity < 0) throw new InvalidOperationException("原料の所持数を確認できません");
            var old = counts[slot.ItemId];
            counts[slot.ItemId] = slot.IsCollectable
                ? old with { Collectable = checked(old.Collectable + slot.Quantity) }
                : old with { Normal = checked(old.Normal + slot.Quantity) };
        }
        return counts;
    }

    public static void RequireNormalFree(IReadOnlyDictionary<uint, ReductionStock> counts, IEnumerable<uint> sources, Func<uint, string> name)
    {
        foreach (var id in sources.Distinct())
        {
            if (!counts.TryGetValue(id, out var count) || count.Normal < 0 || count.Collectable < 0)
                throw new InvalidOperationException("原料の通常品・収集品の所持数を確認できません");
            if (count.Normal > 0)
                throw new InvalidOperationException($"「{name(id)}」の通常品を{count.Normal}個持っています。通常品を鞄から移してから開始してください（収集品は残せます）");
        }
    }

    public static bool CanReduce(IReadOnlyDictionary<uint, ReductionStock> counts)
        => counts.Values.Any(c => c.Collectable > 0);

    public static bool DidReduce(IReadOnlyDictionary<uint, ReductionStock> before, IReadOnlyDictionary<uint, ReductionStock> after)
        => before.Any(p => after.TryGetValue(p.Key, out var count) && count.Collectable < p.Value.Collectable);
}
