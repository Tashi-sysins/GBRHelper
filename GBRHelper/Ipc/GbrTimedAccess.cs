using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using GBRHelper.Features;
using Lumina.Excel.Sheets;
using Action = System.Action;

namespace GBRHelper.Ipc;

/// <summary>確認済みのGBR 7.5.6.1のメンバーだけを使用。版が合わなければ開始しない。</summary>
public sealed class GbrTimedAccess(GbrConfigAccess shared)
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private object Plugin => shared.Plugin() ?? throw new InvalidOperationException("GBRが読み込まれていません");
    public object Auto => Plugin.GetType().GetProperty("AutoGather", Static)?.GetValue(null)
        ?? throw new MissingMemberException("GBR.AutoGather");
    public object? TryGetAuto()
    {
        try { return Auto; }
        catch (Exception) { return null; }
    }
    private object Config => Plugin.GetType().GetProperty("Config", Static)?.GetValue(null)
        ?? throw new MissingMemberException("GBR.Config");
    private object Settings => Config.GetType().GetProperty("AutoGatherConfig")?.GetValue(Config)
        ?? throw new MissingMemberException("GBR.AutoGatherConfig");

    public string ReadSetting(string name)
        => Settings.GetType().GetProperty(name)?.GetValue(Settings)?.ToString() ?? throw new MissingMemberException(name);
    public void SetSetting(string name, string value)
    {
        var settings = Settings;
        var p = settings.GetType().GetProperty(name) ?? throw new MissingMemberException(name);
        p.SetValue(settings, p.PropertyType.IsEnum ? Enum.Parse(p.PropertyType, value) : bool.Parse(value));
        Config.GetType().GetMethod("Save", Type.EmptyTypes)!.Invoke(Config, null);
        if (ReadSetting(name) != value) throw new InvalidOperationException($"GBR設定{name}を書けません");
    }

    public bool TaskBusy
    {
        get
        {
            var a = Auto;
            var task = a.GetType().GetProperty("TaskManager")!.GetValue(a)!;
            return (bool)(task.GetType().GetProperty("IsBusy")!.GetValue(task)!);
        }
    }

    public bool SafeBoundary => Svc.Framework.IsInFrameworkUpdateThread && Me.Available && !TaskBusy &&
        !Svc.Condition[ConditionFlag.Gathering] && !Svc.Condition[ConditionFlag.ExecutingGatheringAction] &&
        !Svc.Condition[ConditionFlag.InCombat] && !Svc.Condition[ConditionFlag.BetweenAreas] &&
        !Svc.Condition[ConditionFlag.BetweenAreas51] && !Svc.Condition[ConditionFlag.Casting] &&
        !Svc.Condition[ConditionFlag.Occupied] && !Svc.Condition[ConditionFlag.Occupied33] &&
        !Svc.Condition[ConditionFlag.Occupied38] && !Svc.Condition[ConditionFlag.Occupied39] &&
        !Svc.Condition[ConditionFlag.Unconscious] && !Svc.Condition[ConditionFlag.Fishing] &&
        !Svc.Condition[ConditionFlag.OccupiedInEvent] && !Svc.Condition[ConditionFlag.OccupiedInQuestEvent];

    /// <summary>
    /// GBR の自動採集が止まっているときに、GBR の Auto-Gather のリストを書いてよいか（2026-10-05）。
    /// 以前は書く前にも採集の切れ目（SafeBoundary：GBR の作業中でない・会話中や詠唱中などでない）を待っていて、
    /// その状態が続くと理由も出さずに待ち続け、解放採取・補充のチェックを入れても GBR にリストができなかった。
    /// GBR が止まっていれば GBR はリストを使わないので、書くのにこの厳しい条件は要らない。
    /// </summary>
    public bool CanWriteListsWhileStopped => Svc.Framework.IsInFrameworkUpdateThread && Me.Available;

    /// <summary>採集の切れ目（SafeBoundary）でない理由（画面に出す）。切れ目なら null。</summary>
    public string? BoundaryProblem()
    {
        if (!Svc.Framework.IsInFrameworkUpdateThread) return "ゲームの更新の流れの外です";
        if (!Me.Available) return "キャラクターを読めません";
        if (TaskBusy) return "GBR が作業中です";
        foreach (var (flag, name) in new (ConditionFlag, string)[]
                 {
                     (ConditionFlag.Gathering, "採集中"), (ConditionFlag.ExecutingGatheringAction, "採集の動作中"),
                     (ConditionFlag.InCombat, "戦闘中"), (ConditionFlag.BetweenAreas, "エリア移動中"), (ConditionFlag.BetweenAreas51, "エリア移動中"),
                     (ConditionFlag.Casting, "詠唱中"), (ConditionFlag.Occupied, "ほかの操作中"), (ConditionFlag.Occupied33, "ほかの操作中"),
                     (ConditionFlag.Occupied38, "ほかの操作中"), (ConditionFlag.Occupied39, "ほかの操作中"), (ConditionFlag.Unconscious, "戦闘不能"),
                     (ConditionFlag.Fishing, "釣り中"), (ConditionFlag.OccupiedInEvent, "会話・イベント中"), (ConditionFlag.OccupiedInQuestEvent, "会話・イベント中"),
                 })
        {
            if (Svc.Condition[flag]) return $"{name}（{flag}）";
        }

        return null;
    }

    public bool NextIsOnce()
    {
        var a = Auto;
        var active = a.GetType().GetField("_activeItemList", Instance)!.GetValue(a)!;
        var next = active.GetType().GetMethod("GetNextOrDefault")!.Invoke(active, null)!;
        var location = next.GetType().GetProperty("Location")!.GetValue(next);
        return location?.GetType().GetProperty("NodeType")?.GetValue(location)?.ToString() is "Unspoiled" or "Legendary";
    }

    public void CheckContract()
    {
        var a = Auto;
        _ = TaskBusy;
        _ = ReadSetting("SortingMethod"); _ = ReadSetting("DoReduce"); _ = ReadSetting("AlwaysReduceAllItems");
        if (a.GetType().GetMethod("ReduceItems", Instance, null, [typeof(bool), typeof(Action)], null) is null
            || a.GetType().GetMethod("HasReducibleItems", Instance) is null
            || a.GetType().GetField("_activeItemList", Instance) is null)
            throw new MissingMemberException("GBRの精選・採集順の連携先が一致しません");
    }

    public bool StartReduction(Action complete)
    {
        if (!SafeBoundary || Svc.Condition[ConditionFlag.Mounted]) return false;
        var a = Auto;
        if (a.GetType().GetMethod("HasReducibleItems", Instance)!.Invoke(a, null) is not true)
            throw new InvalidOperationException("精選できません。精選の解放状態・GBR設定を確認してください");
        a.GetType().GetMethod("ReduceItems", Instance, null, [typeof(bool), typeof(Action)], null)!.Invoke(a, [true, complete]);
        return true;
    }

    /// <summary>GBRの精選は鞄内全体が対象。選ばれていない精選可能品があれば先に止める。</summary>
    public static unsafe Dictionary<uint, ReductionStock> CheckReductionInventory(IReadOnlySet<uint> allowed)
    {
        if (!Svc.Framework.IsInFrameworkUpdateThread || !Svc.PlayerState.IsLoaded) throw new InvalidOperationException("鞄を読めません");
        var inv = InventoryManager.Instance();
        if (inv == null) throw new InvalidOperationException("鞄を読めません");
        var slots = new List<ReductionInventory.Slot>();
        foreach (var bag in new[] { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 })
        {
            var container = inv->GetInventoryContainer(bag);
            if (container == null || !container->IsLoaded) throw new InvalidOperationException("鞄の読込み待ちです");
            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null || item->Quantity == 0) continue;
                var id = item->GetBaseItemId();
                var collectable = item->Flags.HasFlag(InventoryItem.ItemFlags.Collectable);
                slots.Add(new(id, checked((int)item->Quantity), collectable));
                var data = Svc.Data.GetExcelSheet<Item>().GetRow(id);
                if (collectable && data.AetherialReduce != 0 && !allowed.Contains(id))
                    throw new InvalidOperationException($"対象外の精選可能品「{data.Name}」を鞄から移してから開始してください");
            }
        }
        return ReductionInventory.Count(slots, allowed);
    }

    public IReadOnlyList<AethersandRecipe> LoadRecipes(GatherableCatalog catalog)
    {
        var recipes = new List<AethersandRecipe>();
        foreach (var (output, source) in ReductionRows(catalog))
        {
            var name = Svc.Data.GetExcelSheet<Item>().GetRow(output).Name.ToString();
            if (!name.Contains("霊砂", StringComparison.Ordinal) && !name.Contains("aethersand", StringComparison.OrdinalIgnoreCase)) continue;
            recipes.Add(new AethersandRecipe(output, name, source));
        }
        return recipes.Distinct().ToArray();
    }

    /// <summary>
    /// 精選でクリスタル・クラスターが出る原料（2026-10-05 クリスタル・クラスター）。
    /// 属性は出る品の名前で見分ける（「ファイアクリスタル」「ファイアクラスター」の「ファイア」。英語版は「Fire Crystal」「Fire Cluster」）。
    /// 霊砂（名前に「霊砂」）と同じく、区分の番号は埋め込まない。クリスタルとクラスターの両方が見つかった属性だけを返す。
    /// 2026-10-05 の確認：導入版の対応表で、精選でクリスタル・クラスターが出る原料は 62 品（ほぼすべてが両方出る）。
    /// </summary>
    public IReadOnlyList<CrystalRecipe> LoadCrystalRecipes(GatherableCatalog catalog)
    {
        var items = Svc.Data.GetExcelSheet<Item>();
        var byElement = new Dictionary<string, (uint Crystal, uint Cluster, HashSet<uint> Sources)>();
        foreach (var (output, source) in ReductionRows(catalog))
        {
            var name = items.GetRow(output).Name.ToString();
            if (CrystalPlan.Element(name) is not { } element) continue;
            if (!byElement.TryGetValue(element.Name, out var e)) e = (0, 0, new HashSet<uint>());
            if (element.Cluster) e.Cluster = output; else e.Crystal = output;
            e.Sources.Add(source);
            byElement[element.Name] = e;
        }
        return byElement.Where(p => p.Value.Crystal != 0 && p.Value.Cluster != 0)
            .SelectMany(p => p.Value.Sources.Select(s => new CrystalRecipe(p.Value.Crystal, p.Key, p.Value.Crystal, p.Value.Cluster, s)))
            .OrderBy(r => r.CrystalId).ThenBy(r => r.SourceId).ToArray();
    }

    /// <summary>
    /// 精選の対応表（LuminaSupplemental の ItemSupplement.csv の種類 2）のうち、原料が刻限などの時限の収集品の行（出る品, 原料）。
    /// GBR と同じロード文脈の補足データを使用。別プラグインの任意の最新版へ混ぜない。
    /// </summary>
    private IEnumerable<(uint Output, uint Source)> ReductionRows(GatherableCatalog catalog)
    {
        var assembly = Plugin.GetType().Assembly;
        var dependency = assembly.GetReferencedAssemblies().Single(x => x.Name == "LuminaSupplemental.Excel");
        var data = AssemblyLoadContext.GetLoadContext(assembly)!.LoadFromAssemblyName(dependency);
        var resource = data.GetManifestResourceNames().Single(x => x.EndsWith("ItemSupplement.csv", StringComparison.Ordinal));
        using var stream = new StreamReader(data.GetManifestResourceStream(resource)!);
        var sources = catalog.Entries.Where(e => e.Collectable && MaterialPlan.IsTimed(e))
            .Select(e => e.ItemId).ToHashSet();
        var rows = new List<(uint, uint)>();
        stream.ReadLine();
        while (stream.ReadLine() is { } line)
        {
            var parts = line.Split(',');
            if (parts.Length < 6 || parts[2] != "2" || !uint.TryParse(parts[0], out var output)
                || !uint.TryParse(parts[1], out var source) || !sources.Contains(source)) continue;
            rows.Add((output, source));
        }
        return rows;
    }
}

public sealed record AethersandRecipe(uint OutputId, string Name, uint SourceId);

/// <summary>精選でクリスタル・クラスターが出る原料 1 品。ElementKey はその属性のクリスタルの品番（属性の見分けに使う）。</summary>
public sealed record CrystalRecipe(uint ElementKey, string ElementName, uint CrystalId, uint ClusterId, uint SourceId);
