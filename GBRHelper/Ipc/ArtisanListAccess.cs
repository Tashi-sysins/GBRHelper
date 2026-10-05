using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace GBRHelper.Ipc;

/// <summary>
/// Artisan の Crafting Lists を読む（要望「Crafting Listsから末端素材抽出」）。
/// GBR 自身の「Artisan から読み込む」（GatherBuddy/AutoGather/Helpers/Reflection.cs の ArtisanExporter）と同じ道筋：
///   ・リストの一覧：Artisan の本体の Config.NewCraftingLists の各リストの ID と Name（GetArtisanListNames と同じ。既製のリストは入らない）
///   ・素材：Artisan の Artisan.CraftingLists.CraftingListFunctions.ListMaterials(リスト)（Artisan-main CraftingList.cs 176-192）
///     ＝リストの各行（スキップ・数 0 の行を除く）のレシピの、直下の材料を「行の数（製作の回数）」倍して足したもの。
///     中間素材の行がリストにあれば、その材料も入る。手持ちは差し引かない（GBR の取り込みと同じ）。
///     ListMaterials は、行の設定（ListItemOptions）が無いときに作って Artisan の設定を保存する（Artisan の作り。GBR の取り込みでも同じ）。
/// Artisan の本体は Dalamud の読み込み済みプラグインの一覧から取り出す（GbrConfigAccess.Plugin と同じ経路）。ゲームの更新の流れから呼ぶ。
/// </summary>
public sealed class ArtisanListAccess
{
    public const string InternalName = "Artisan";

    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private object? entry;
    private FieldInfo? instanceField;
    private object? plugin;
    private DateTime nextScan = DateTime.MinValue;

    /// <summary>最後に失敗した理由（画面に出す）。</summary>
    public string LastError { get; private set; } = "";

    /// <summary>Crafting Lists の一覧（ID → 名前）。Artisan が無い・読めなければ null（理由は LastError。0 件とは区別する）。</summary>
    public Dictionary<int, string>? ListNames()
    {
        try
        {
            if (this.Lists() is not { } lists)
                return null;
            var result = new Dictionary<int, string>();
            foreach (var list in lists)
            {
                if (list is null) continue;
                var id = Member(list, "ID") is int i ? i : (int?)null;
                var name = Member(list, "Name")?.ToString();
                if (id is { } key && name is not null)
                    result[key] = name;
            }

            this.LastError = "";
            return result;
        }
        catch (Exception ex)
        {
            this.LastError = "Artisan の Crafting Lists を読めません：" + ex.GetBaseException().Message;
            return null;
        }
    }

    /// <summary>そのリストの素材（品番 → 数）。リストが無い・読めなければ null（理由は LastError）。</summary>
    public Dictionary<uint, int>? Materials(int listId)
    {
        try
        {
            if (this.Lists() is not { } lists || this.plugin is null)
                return null;
            var target = lists.Cast<object?>().FirstOrDefault(l => l is not null && Member(l, "ID") is int id && id == listId);
            if (target is null)
            {
                this.LastError = $"Artisan に Crafting List（ID {listId}）が見つかりません。消された可能性があります";
                return null;
            }

            var type = this.plugin.GetType().Assembly.GetType("Artisan.CraftingLists.CraftingListFunctions");
            var method = type?.GetMethods(All).FirstOrDefault(m => m.Name == "ListMaterials" && m.GetParameters().Length == 1);
            if (method is null)
            {
                this.LastError = "Artisan の素材の一覧の関数（CraftingListFunctions.ListMaterials）が見つかりません（Artisan の版が変わった可能性）";
                return null;
            }

            if (method.Invoke(null, [target]) is not Dictionary<uint, int> materials)
            {
                this.LastError = "Artisan の素材の一覧が想定と違う形です";
                return null;
            }

            this.LastError = "";
            return new Dictionary<uint, int>(materials);
        }
        catch (Exception ex)
        {
            this.LastError = "Artisan の素材を読めません：" + ex.GetBaseException().Message;
            return null;
        }
    }

    /// <summary>Artisan の Config.NewCraftingLists。届かなければ null。</summary>
    private IEnumerable? Lists()
    {
        if (this.Plugin() is not { } p)
            return null;
        var config = Member(p, "Config");
        if (config is null)
        {
            this.LastError = "Artisan の設定（Config）に届きません";
            return null;
        }

        if (Member(config, "NewCraftingLists") is not IEnumerable lists)
        {
            this.LastError = "Artisan の Crafting Lists（NewCraftingLists）に届きません";
            return null;
        }

        return lists;
    }

    /// <summary>フィールドかプロパティ（GBR の GetFoP と同じ）。</summary>
    private static object? Member(object target, string name)
    {
        var t = target.GetType();
        return t.GetProperty(name, All)?.GetValue(target) ?? t.GetField(name, All)?.GetValue(target);
    }

    /// <summary>Artisan の本体（未導入・未読み込みなら null）。読み直されたら取り直す。</summary>
    private object? Plugin()
    {
        if (this.plugin is not null && this.entry is not null && this.instanceField is not null
            && ReferenceEquals(this.instanceField.GetValue(this.entry), this.plugin))
            return this.plugin;

        this.plugin = null;
        this.entry = null;
        this.instanceField = null;
        if (DateTime.UtcNow < this.nextScan)
        {
            this.LastError = "Artisan が読み込まれていません";
            return null;
        }

        this.nextScan = DateTime.UtcNow.AddSeconds(5);
        try
        {
            var dalamud = Svc.PluginInterface.GetType().Assembly;
            var service = dalamud.GetType("Dalamud.Service`1", throwOnError: true)!;
            var pmType = dalamud.GetType("Dalamud.Plugin.Internal.PluginManager", throwOnError: true)!;
            var pm = service.MakeGenericType(pmType).GetMethod("Get", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
            var installed = (IEnumerable)pmType.GetProperty("InstalledPlugins", BindingFlags.Public | BindingFlags.Instance)!.GetValue(pm)!;
            foreach (var e in installed)
            {
                var et = e.GetType();
                if (et.GetProperty("InternalName")?.GetValue(e) as string != InternalName || et.GetProperty("IsLoaded")?.GetValue(e) is not true)
                    continue;
                FieldInfo? field = null;
                for (var t = et; t != null && field == null; t = t.BaseType)
                    field = t.GetField("instance", BindingFlags.NonPublic | BindingFlags.Instance);
                if (field?.GetValue(e) is not { } instance)
                    continue;
                this.entry = e;
                this.instanceField = field;
                this.plugin = instance;
                return instance;
            }

            this.LastError = "Artisan が読み込まれていません";
        }
        catch (Exception ex)
        {
            this.LastError = "Artisan の本体に届きません：" + ex.GetBaseException().Message;
        }

        return null;
    }
}
