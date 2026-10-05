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

    /// <summary>
    /// 管理リストが、同じ場所（FolderPath）のほかのどのリストよりも上（Order が小さい）に保存されているか。リストが 1 つに決まらなければ null。
    /// GBR は同じ場所のリストを Order の小さい順（同じなら名前順）に並べる（GBR 7.5.6.1 の ManualOrderSortMode.GetChildren）。
    /// 同じ数のときは名前で決まるので、上とは数えない（GBR の MoveList で動かすと必ず小さくなる）。
    /// </summary>
    public static bool? IsFirstInFolder(string json, string name, string tag)
        => AreFirstInFolder(json, [name], tag);

    /// <summary>
    /// 管理リスト（names の順）が、それぞれの場所（FolderPath）で一番上からこの順に並んで保存されているか。1 つでも 1 つに決まらなければ null。
    /// 同じ場所の管理リストは names の順に Order が小さくなっていて、その場所のほかのどのリストよりも Order が小さいこと
    /// （2026-10-06 霊砂とクリスタルを同時に登録できるようにした。霊砂のリストを一番上、クリスタルのリストをその下に置く）。
    /// </summary>
    public static bool? AreFirstInFolder(string json, IReadOnlyList<string> names, string tag)
    {
        using var doc = JsonDocument.Parse(json);
        var rows = doc.RootElement.EnumerateArray().ToArray();
        var owned = new List<int>();
        foreach (var name in names)
        {
            var found = Enumerable.Range(0, rows.Length).Where(i =>
                rows[i].GetProperty("Name").GetString() == name &&
                (rows[i].GetProperty("Description").GetString() ?? "").Contains(tag, StringComparison.Ordinal)).ToArray();
            if (found.Length != 1)
                return null;
            owned.Add(found[0]);
        }

        foreach (var group in owned.GroupBy(i => FolderPath(rows[i])))
        {
            var mine = group.Select(i => Order(rows[i])).ToArray(); // names の順
            if (mine.Zip(mine.Skip(1)).Any(p => p.First >= p.Second))
                return false;
            var others = Enumerable.Range(0, rows.Length).Where(i => !owned.Contains(i) && FolderPath(rows[i]) == group.Key);
            if (others.Any(i => Order(rows[i]) <= mine[^1]))
                return false;
        }

        return true;

        static string FolderPath(JsonElement row)
            => row.TryGetProperty("FolderPath", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() ?? "" : "";
        static int Order(JsonElement row)
            => row.TryGetProperty("Order", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : 0;
    }
}
