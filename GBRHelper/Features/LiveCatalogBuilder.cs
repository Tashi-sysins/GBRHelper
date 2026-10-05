using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GBRHelper.Ipc;
using Lumina.Excel.Sheets;

namespace GBRHelper.Features;

/// <summary>
/// GBR の Gatherables から表示と採取履歴判定に必要な値を取得する。
/// Log 列の「×」は GatheringCompletionReader で判定し、収集品等の「－」は区別する。
/// </summary>
public sealed class LiveCatalogBuilder
{
    private const BindingFlags PubInst = BindingFlags.Public | BindingFlags.Instance;
    private const BindingFlags PubStatic = BindingFlags.Public | BindingFlags.Static;

    private readonly GbrConfigAccess shared;

    public LiveCatalogBuilder(GbrConfigAccess shared)
    {
        this.shared = shared;
    }

    /// <summary>直近の失敗。無ければ空。</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>GBR 未起動・Lumina 失敗なら null。</summary>
    public GatherableCatalog? Build()
    {
        try
        {
            // GBR の Gatherables 全体を使い、ベンチャー対象には限定しない。
            var gbrItems = this.LoadGbrGatherables();
            if (gbrItems is null)
                return null;

            // ベンチャーで依頼できる品の印（読めなくても一覧は出す。絞り込みの設定だけ使えなくなる）
            var venture = LoadVentureItemIds(out var ventureError);

            // 純粋ロジックに渡して組み立て
            var cat = GatherableCatalog.BuildGatherables(gbrItems, venture);
            this.LastError = ventureError;
            return cat;
        }
        catch (Exception ex)
        {
            this.LastError = $"GatherableCatalog の構築に失敗: {ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// ベンチャー（RetainerTaskNormal）の採掘・園芸の行に出る品。読めなければ null と理由。
    /// 2026-10-05 にゲームデータで数えた値：採掘・園芸で 654 種。GBR の Log 判定の対象 694 行のうち
    /// 約 70 行（復興用の品・Lv50 未知のクラスター・刻限の品・Lv100 伝説の品）はここに無い＝依頼できない。
    /// </summary>
    private static HashSet<uint>? LoadVentureItemIds(out string error)
    {
        try
        {
            var set = new HashSet<uint>();
            foreach (var row in Svc.Data.GetExcelSheet<RetainerTaskNormal>())
            {
                // GatheringLog が 0 の行は釣り（FishingLog）など採掘・園芸以外
                if (row.Item.RowId != 0 && row.GatheringLog.RowId != 0)
                    set.Add(row.Item.RowId);
            }

            error = set.Count == 0 ? "ベンチャーの依頼品を 1 件も読めませんでした（「ベンチャーで依頼できる品だけ」は使えません）" : string.Empty;
            return set.Count == 0 ? null : set;
        }
        catch (Exception ex)
        {
            error = $"ベンチャーの依頼品を読めません（「ベンチャーで依頼できる品だけ」は使えません）: {ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    private IReadOnlyDictionary<uint, GbrGatherableView>? LoadGbrGatherables()
    {
        if (this.shared.Plugin() is not { } p)
        {
            this.LastError = this.shared.LastError;
            return null;
        }

        var gameData = p.GetType().GetProperty("GameData", PubStatic)?.GetValue(null);
        if (gameData is null)
        {
            this.LastError = "GBR の GameData（public static）に届きません";
            return null;
        }

        var gatherables = gameData.GetType().GetProperty("Gatherables", PubInst)?.GetValue(gameData);
        if (gatherables is null)
        {
            this.LastError = "GBR の GameData.Gatherables に届きません";
            return null;
        }

        var result = new Dictionary<uint, GbrGatherableView>();

        // IDictionary 経由で列挙する。FrozenDictionary の非ジェネリック経路が
        // 落ちる環境では、Values だけ IEnumerable で引く経路に切り替える。
        var enumerated = false;

        if (gatherables is IDictionary dict)
        {
            try
            {
                foreach (DictionaryEntry e in dict)
                {
                    if (e.Key is not uint itemId || e.Value is null)
                        continue;
                    var view = this.ExtractView(itemId, e.Value);
                    if (view is null)
                        throw new InvalidOperationException($"Item {itemId} の Log 判定に必要な情報を読めません");
                    result[itemId] = view;
                }
                enumerated = true;
            }
            catch
            {
                // 落ちたら次の経路へ
            }
        }

        if (!enumerated)
        {
            result.Clear();
            // IEnumerable 経路：Values を列挙して ItemId を各要素から取る
            var values = gatherables.GetType().GetProperty("Values", PubInst)?.GetValue(gatherables) as IEnumerable;
            if (values is null)
            {
                this.LastError = "GBR の Gatherables の列挙に失敗（IDictionary も Values も使えない）";
                return null;
            }

            foreach (var item in values)
            {
                if (item is null)
                    continue;
                var idObj = item.GetType().GetProperty("ItemId", PubInst)?.GetValue(item);
                if (idObj is not uint itemId)
                    continue;
                var view = this.ExtractView(itemId, item);
                if (view is null)
                    throw new InvalidOperationException($"Item {itemId} の Log 判定に必要な情報を読めません");
                result[itemId] = view;
            }
        }

        return result;
    }

    /// <summary>Gatherable 1 個から必要な値だけ取り出す。</summary>
    private GbrGatherableView? ExtractView(uint itemId, object gatherable)
    {
        try
        {
            var t = gatherable.GetType();

            // Level
            var levelObj = t.GetProperty("Level", PubInst)?.GetValue(gatherable);
            if (levelObj is not int level)
                return null;

            // GatheringType（GBR の enum）→ 自分の enum に数値で写す
            var gtObj = t.GetProperty("GatheringType", PubInst)?.GetValue(gatherable);
            if (gtObj is null)
                return null;
            var gtValue = (byte)Convert.ChangeType(gtObj, typeof(byte));
            var gt = (GatherableCatalog.GbrGatheringType)gtValue;

            // GatheringId
            var gidObj = t.GetProperty("GatheringId", PubInst)?.GetValue(gatherable);
            var gid = gidObj is uint g ? g : 0u;

            // MultiString.ToString() は英語固定のため、日本語のフィールドを使う。
            var nameObj = t.GetProperty("Name", PubInst)?.GetValue(gatherable);
            var name = nameObj?.GetType().GetField("Japanese", PubInst)?.GetValue(nameObj) as string;
            if (string.IsNullOrWhiteSpace(name)) name = nameObj?.ToString() ?? $"Item {itemId}";

            // GBR の ExtendedGatherable.NotTrackedByGatheringLog と同じ条件。
            // メンバーが読めないときに false と推測して「×」へ混ぜない。
            var itemData = t.GetProperty("ItemData", PubInst)?.GetValue(gatherable)
                ?? throw new InvalidOperationException("ItemData が読めません");
            var dataType = itemData.GetType();
            var collectable = (bool)(dataType.GetProperty("IsCollectable")?.GetValue(itemData)
                ?? throw new InvalidOperationException("IsCollectable が読めません"));
            var always = (bool)(dataType.GetProperty("AlwaysCollectable")?.GetValue(itemData)
                ?? throw new InvalidOperationException("AlwaysCollectable が読めません"));
            var category = dataType.GetProperty("ItemSearchCategory")?.GetValue(itemData)
                ?? throw new InvalidOperationException("ItemSearchCategory が読めません");
            var categoryId = (uint)(category.GetType().GetProperty("RowId")?.GetValue(category)
                ?? throw new InvalidOperationException("ItemSearchCategory.RowId が読めません"));
            var treasure = (bool)(t.GetProperty("IsTreasureMap", PubInst)?.GetValue(gatherable)
                ?? throw new InvalidOperationException("IsTreasureMap が読めません"));
            var notTracked = collectable || always || treasure || categoryId == 0;

            // 採集点ごとの職（Multiple のときだけ使う）と伝承録（GatheringNode.FolkloreId。Node.Base.cs:96-98）。
            // 伝承録は「すべての採集点で要る」ときだけ要る品とする（GBR の Folklore 列と同じ。Interface.ItemTab.cs:736-739）。
            // 読めないときに「要らない」と推測すると、伝承録の無い人の GBR を止める品が混ざるので、読めなければ失敗にする。
            var nodeKinds = MaterialNodeKinds.None;
            var nodeTypes = new List<GatherableCatalog.GbrGatheringType>();
            var nodeFolklore = new List<uint>();
            var uptime = 0u;
            if (t.GetProperty("NodeList", PubInst)?.GetValue(gatherable) is not IEnumerable nodes)
                throw new InvalidOperationException("NodeList が読めません");

            foreach (var node in nodes)
            {
                if (node is null)
                    continue;
                var nt = node.GetType();

                // 採集点が出る時刻（エオルゼア時間の 0〜23 時のビット）。GBR の GatheringNode.Times（BitfieldUptime.IsUp(uint)。
                // Node.Base.cs:23・BitfieldUptime.cs:27）。霊砂・クリスタルの原料の並び（CrystalPlan）に使う。読めなければ 0（不明）。
                uptime |= UptimeHours(nt.GetProperty("Times", PubInst)?.GetValue(node));
                nodeKinds |= nt.GetProperty("NodeType", PubInst)?.GetValue(node)?.ToString() switch
                {
                    "Regular" => MaterialNodeKinds.Regular,
                    "Unspoiled" or "Legendary" => MaterialNodeKinds.Once,
                    "Ephemeral" => MaterialNodeKinds.Repeat,
                    _ => MaterialNodeKinds.Other,
                };
                if (gt == GatherableCatalog.GbrGatheringType.Multiple
                    && nt.GetProperty("GatheringType", PubInst)?.GetValue(node) is { } value)
                    nodeTypes.Add((GatherableCatalog.GbrGatheringType)Convert.ToByte(value));

                if (nt.GetProperty("FolkloreId", PubInst)?.GetValue(node) is not uint folklore)
                    throw new InvalidOperationException("採集点の FolkloreId が読めません");
                nodeFolklore.Add(folklore);
            }

            return new GbrGatherableView(itemId, name, level, gid, gt, notTracked, nodeTypes,
                GatherableCatalog.RequiredFolkloreBooks(nodeFolklore), collectable || always, treasure, nodeKinds, uptime);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>BitfieldUptime から、出ている時刻（0〜23 時）のビットを作る。読めなければ 0。</summary>
    private static uint UptimeHours(object? times)
    {
        if (times?.GetType().GetMethod("IsUp", PubInst, [typeof(uint)]) is not { } isUp)
            return 0;
        var mask = 0u;
        for (var hour = 0u; hour < 24; hour++)
            if (isUp.Invoke(times, [hour]) is true)
                mask |= 1u << (int)hour;
        return mask;
    }
}
