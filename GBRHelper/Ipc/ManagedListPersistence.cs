using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace GBRHelper.Ipc;

/// <summary>GBRのConfig配列を読み、名前だけでなく管理タグ・内容を照合する。書込みはしない。</summary>
public static class ManagedListPersistence
{
    public static bool Matches(string json, string name, string tag,
        IReadOnlyList<(uint ItemId, uint Quantity)>? entries, bool enabled)
    {
        using var doc = JsonDocument.Parse(json);
        var owned = doc.RootElement.EnumerateArray().Where(x =>
            x.GetProperty("Name").GetString() == name &&
            (x.GetProperty("Description").GetString() ?? "").Contains(tag, StringComparison.Ordinal)).ToArray();
        if (entries is null) return owned.Length == 0;
        if (owned.Length != 1) return false;
        var row = owned[0];
        if (row.GetProperty("Enabled").GetBoolean() != enabled || row.GetProperty("Fallback").GetBoolean()
            || row.GetProperty("RemoveCompletedItems").GetBoolean()) return false;
        if (!row.GetProperty("ItemIds").EnumerateArray().Select(x => x.GetUInt32()).SequenceEqual(entries.Select(x => x.ItemId))) return false;
        var quantities = row.GetProperty("Quantities");
        var flags = row.GetProperty("EnabledItems");
        return quantities.EnumerateObject().Count() == entries.Count && entries.All(e =>
            quantities.TryGetProperty(e.ItemId.ToString(), out var q) && q.GetUInt32() == e.Quantity &&
            flags.TryGetProperty(e.ItemId.ToString(), out var f) && f.GetBoolean());
    }

    /// <summary>管理リストの 1 品の有効・無効が保存されているか。リストが 1 つに決まらない・品が無いときは null。</summary>
    public static bool? ItemEnabledMatches(string json, string name, string tag, uint itemId, bool enabled)
    {
        using var doc = JsonDocument.Parse(json);
        var owned = doc.RootElement.EnumerateArray().Where(x =>
            x.GetProperty("Name").GetString() == name &&
            (x.GetProperty("Description").GetString() ?? "").Contains(tag, StringComparison.Ordinal)).ToArray();
        if (owned.Length != 1 || !owned[0].GetProperty("EnabledItems").TryGetProperty(itemId.ToString(), out var f))
            return null;
        return f.GetBoolean() == enabled;
    }
}
