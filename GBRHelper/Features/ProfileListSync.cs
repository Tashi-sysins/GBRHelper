using System;
using System.Collections.Generic;
using System.Linq;

namespace GBRHelper.Features;

/// <summary>削除保存が失敗しても再試行対象を残す。GBR停止を各書込みの直前に確認する。</summary>
public sealed class ProfileListSync
{
    /// <param name="Name">設定の記録の鍵（GatherProfiles.Name。キャラクターの番号入り）。</param>
    /// <param name="UsesRetainerInventory">GBR がこのリストの品の所持数にリテイナーの在庫も足すか（全素材の補充はいま false）。</param>
    /// <param name="ListName">GBR に作るリストの名前（GatherProfiles.ListName）。null なら Name と同じ。</param>
    public sealed record Plan(string Name, string Tag, List<(uint ItemId, uint Quantity)> Entries, bool Enabled = true,
        bool UsesRetainerInventory = true, string? ListName = null)
    {
        /// <summary>GBR のリストの名前（書く・比べるときはこちら）。</summary>
        public string GbrName => this.ListName ?? this.Name;
    }
    private readonly HashSet<(string Name, string Tag)> pendingDeletes = [];
    public bool HasPendingDeletes => pendingDeletes.Count != 0;
    public void Apply(IEnumerable<GatherProfiles.Owned> old, IReadOnlyList<Plan> plans,
        Action ensureStopped, Func<string, string, bool> remove, Func<Plan, bool> write)
    {
        foreach (var item in old) pendingDeletes.Add((item.Name, item.Tag));
        foreach (var item in pendingDeletes.ToArray())
        {
            ensureStopped();
            if (!remove(item.Name, item.Tag)) throw new InvalidOperationException("旧リストの削除を保存できません");
            pendingDeletes.Remove(item);
        }
        foreach (var plan in plans)
        {
            ensureStopped();
            if (!write(plan)) throw new InvalidOperationException("新しいリストを保存できません");
        }
    }
}
