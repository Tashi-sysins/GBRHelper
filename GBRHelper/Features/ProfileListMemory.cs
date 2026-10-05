using System;
using System.Collections.Generic;
using System.Linq;
using GBRHelper.Ipc;

namespace GBRHelper.Features;

/// <summary>自動生成した品と利用者の追加を分ける。キャラを切り替えて実リストを消しても保持する。</summary>
public sealed class ProfileListMemory
{
    public HashSet<uint> GeneratedIds { get; set; } = [];
    public HashSet<uint> PendingGeneratedIds { get; set; } = [];
    public Dictionary<uint, uint> ManualRows { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public ProfileListMemory Copy() => new() { GeneratedIds = new(GeneratedIds), PendingGeneratedIds = new(PendingGeneratedIds), ManualRows = new(ManualRows), Enabled = Enabled };
    /// <param name="addedDisabledByUs">
    /// GBR の採集中に、こちらが無効の状態で先に足したリストか（Configuration.GatherListAddedDisabled）。
    /// そうなら、無効になっているのは利用者が選んだことではないので、無効としては覚えない（前に覚えた有効・無効のまま。GBR が止まったら有効にするため）。
    /// </param>
    public static ProfileListMemory Capture(ProfileListMemory? previous, GbrAutoGatherListAccess.ListSummary list, bool reset = false,
        bool addedDisabledByUs = false)
    {
        var value = previous?.Copy() ?? new();
        if (reset) return value;
        // 生成履歴が無い旧リストの行は、勝手に自動生成品と決めず保護する。
        value.ManualRows = list.Entries.Where(e => (!value.GeneratedIds.Contains(e.ItemId) && !value.PendingGeneratedIds.Contains(e.ItemId)) || value.ManualRows.ContainsKey(e.ItemId))
            .GroupBy(e => e.ItemId).ToDictionary(g => g.Key, g => g.Max(e => e.Quantity));
        value.Enabled = list.Enabled || (addedDisabledByUs && (previous?.Enabled ?? true));
        return value;
    }
    public bool Same(ProfileListMemory other) => Enabled == other.Enabled && GeneratedIds.SetEquals(other.GeneratedIds) && PendingGeneratedIds.SetEquals(other.PendingGeneratedIds)
        && ManualRows.Count == other.ManualRows.Count && ManualRows.All(p => other.ManualRows.TryGetValue(p.Key, out var n) && n == p.Value);
    public static ProfileListSync.Plan Merge(ProfileListSync.Plan generated, ProfileListMemory? saved)
    {
        if (saved is null) return generated;
        var entries = generated.Entries.ToDictionary(e => e.ItemId, e => e.Quantity);
        foreach (var row in saved.ManualRows) entries[row.Key] = Math.Max(entries.GetValueOrDefault(row.Key), row.Value);
        return generated with { Entries = entries.Select(p => (p.Key, p.Value)).ToList(), Enabled = saved.Enabled };
    }
}
