using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using GBRHelper.Ipc;
using Lumina.Excel.Sheets;

namespace GBRHelper.Features;

/// <summary>Allagan Toolsの現在キャラ・各リテイナーの全鞄を確認し、不明を0個にしない。</summary>
public sealed class MaterialInventory(GbrAutoGatherListAccess lists)
{
    public string Error { get; private set; } = "";
    private static readonly uint[] RetainerTypes =
    [
        (uint)InventoryType.RetainerPage1, (uint)InventoryType.RetainerPage2,
        (uint)InventoryType.RetainerPage3, (uint)InventoryType.RetainerPage4,
        (uint)InventoryType.RetainerPage5, (uint)InventoryType.RetainerPage6,
        (uint)InventoryType.RetainerPage7, (uint)InventoryType.RetainerCrystals,
    ];

    public static int Level(GatherableCatalog.Job job)
    {
        if (!Svc.PlayerState.IsLoaded) return 0;
        var row = Svc.Data.GetExcelSheet<ClassJob>().First(x => x.Abbreviation.ToString() ==
            (job == GatherableCatalog.Job.Miner ? "MIN" : "BTN"));
        return Svc.PlayerState.GetClassJobLevel(row);
    }

    public static unsafe int? Local(uint id)
    {
        if (!Svc.Framework.IsInFrameworkUpdateThread || !Svc.PlayerState.IsLoaded || Svc.PlayerState.ContentId == 0) return null;
        var inv = InventoryManager.Instance();
        if (inv == null) return null;
        // GBRと同じ数え方。収集品フラグだけ別に数える。
        var n = inv->GetInventoryItemCount(id, false, false, false, 0);
        if (Svc.Data.GetExcelSheet<Item>().GetRow(id).IsCollectable)
            n += inv->GetInventoryItemCount(id, false, false, false, 1);
        return n >= 0 ? n : null;
    }

    public unsafe Dictionary<uint, int>? Read(IEnumerable<uint> ids, bool retainers)
    {
        try
        {
            var cid = Svc.PlayerState.ContentId;
            if (!Svc.Framework.IsInFrameworkUpdateThread || cid == 0 || !Svc.PlayerState.IsLoaded)
                throw new InvalidOperationException("キャラクター情報の読込みを待っています");
            var retained = new Dictionary<uint, int>();
            if (retainers)
            {
                if (lists.GetCheckRetainers() != true)
                    throw new InvalidOperationException("GBR設定のリテイナー在庫参照をONにしてください");
                var pi = Svc.PluginInterface;
                if (!pi.GetIpcSubscriber<bool>("AllaganTools.IsInitialized").InvokeFunc()
                    || pi.GetIpcSubscriber<ulong>("AllaganTools.CurrentCharacter").InvokeFunc() != cid)
                    throw new InvalidOperationException("Allagan Toolsの現在キャラクターを確認できません");
                var rm = RetainerManager.Instance();
                if (rm == null || !rm->IsReady)
                    throw new InvalidOperationException("リテイナー一覧を読めません。一度呼び鈴で一覧を開いてください");
                var owners = pi.GetIpcSubscriber<bool, HashSet<ulong>>("AllaganTools.GetCharactersOwnedByActive").InvokeFunc(false);
                for (uint i = 0; i < rm->GetRetainerCount(); i++)
                {
                    var ret = rm->GetRetainerBySortedIndex(i);
                    if (ret == null || !owners.Contains(ret->RetainerId))
                        throw new InvalidOperationException("未確認のリテイナーがあります。各リテイナーの荷物を開いてください");
                    var rows = pi.GetIpcSubscriber<ulong, HashSet<ulong[]>>("AllaganTools.GetCharacterItems").InvokeFunc(ret->RetainerId);
                    // Allaganの論理鞄は35枠×5、クリスタル18枠。空き枠もIPCに含まれる。
                    for (var bag = 0; bag < 5; bag++)
                        if (!HasSlots(rows, (uint)InventoryType.RetainerPage1 + (uint)bag, 35, ret->RetainerId))
                            throw new InvalidOperationException("リテイナーの鞄が未確認です。Allagan ToolsをONにして荷物を開いてください");
                    if (!HasSlots(rows, (uint)InventoryType.RetainerCrystals, 18, ret->RetainerId))
                        throw new InvalidOperationException("リテイナーのクリスタル欄が未確認です");
                    foreach (var pair in CountRetainerRows(rows, ret->RetainerId))
                        retained[pair.Key] = (int)Math.Min((long)retained.GetValueOrDefault(pair.Key) + pair.Value, int.MaxValue);
                }
            }
            var result = new Dictionary<uint, int>();
            foreach (var id in ids.Distinct())
            {
                var local = Local(id) ?? throw new InvalidOperationException("本人の所持数を読めません");
                var other = retained.GetValueOrDefault(id);
                result[id] = (int)Math.Min((long)local + other, int.MaxValue);
            }
            if (Svc.PlayerState.ContentId != cid) throw new InvalidOperationException("キャラクターが変わりました");
            Error = "";
            return result;
        }
        catch (Exception ex) { Error = ex.GetBaseException().Message; return null; }
    }

    /// <summary>確認済みの鞄を一度だけ集計する。別所有者・装備・出品欄を混ぜない。</summary>
    public static Dictionary<uint, int> CountRetainerRows(IEnumerable<ulong[]> rows, ulong owner)
    {
        var result = new Dictionary<uint, int>();
        var seen = new HashSet<(ulong Bag, ulong Slot)>();
        foreach (var row in rows)
        {
            if (row.Length < 24) throw new InvalidOperationException("リテイナー在庫の形式を確認できません");
            if (row[23] != owner || !RetainerTypes.Contains((uint)row[20])) continue;
            if (!seen.Add((row[20], row[22]))) throw new InvalidOperationException("リテイナー在庫に重複枠があります");
            if (row[2] == 0 || row[3] == 0) continue;
            var id = checked((uint)row[2]);
            result[id] = (int)Math.Min((ulong)result.GetValueOrDefault(id) + Math.Min(row[3], (ulong)int.MaxValue), (ulong)int.MaxValue);
        }
        return result;
    }

    public static bool HasSlots(IEnumerable<ulong[]> rows, uint bag, int size, ulong owner)
        => rows.Where(r => r.Length >= 24 && r[20] == bag && r[23] == owner && r[22] < (ulong)size)
            .Select(r => r[22]).Distinct().Count() == size;
}
