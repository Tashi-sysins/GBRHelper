using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace GBRHelper.Features;

/// <summary>
/// 「どの区分を未採取リストにするか」のユーザー選択から、
/// GBR に渡す AutoGatherList 作成計画を純粋ロジックとして組み立てる。
///
/// 【設計方針（指示書）】
/// - 計画 ID を持ち、キャラクター切替後に古い計画を適用しない
/// - 基準数（作成時の所持数）と目標数（基準数 + 1）を同じ計画で保持
/// - 毎フレーム目標を書き直さない（計画は作成時に固定、再計画で明示的に差し替え）
/// - Unknown 状態の行はリストに入れない（未採取に変換しない）
/// - NotTracked の行もリストに入れない（本機能では扱わない）
/// - 他リストに同じ品があれば保留（HeldByConflict）
/// - 999999 上限超過は保留（HeldByQuantityCap）
///
/// 【Core/Effects 分離】
/// このクラスは入力を全部受け取り、結果だけ返す。ゲーム・GBR には触らない。
/// </summary>
public sealed class UnvisitedPlan
{
    /// <summary>この計画の一意 ID（キャラ切替時の古い計画の破棄に使う）。</summary>
    public Guid PlanId { get; }

    /// <summary>計画を作ったときのキャラの ContentId。</summary>
    public ulong CharacterContentId { get; }

    /// <summary>選ばれた区分（職 × Lv帯）。</summary>
    public ImmutableArray<Selection> Selections { get; }

    /// <summary>区分ごとの作成リスト。</summary>
    public ImmutableArray<PlannedList> Lists { get; }

    /// <summary>何らかの理由で入れられなかった行。利用者に理由を見せる。</summary>
    public ImmutableArray<HeldEntry> Held { get; }

    public DateTime CreatedAt { get; }

    private UnvisitedPlan(Guid planId, ulong characterContentId, DateTime createdAt,
        ImmutableArray<Selection> selections,
        ImmutableArray<PlannedList> lists,
        ImmutableArray<HeldEntry> held)
    {
        this.PlanId = planId;
        this.CharacterContentId = characterContentId;
        this.CreatedAt = createdAt;
        this.Selections = selections;
        this.Lists = lists;
        this.Held = held;
    }

    /// <summary>選ばれた区分 1 つ。</summary>
    public sealed record Selection(GatherableCatalog.Job Job, GatherableCatalog.LevelBand Band);

    /// <summary>作る AutoGatherList 1 本の計画。</summary>
    public sealed record PlannedList(
        string Name,
        GatherableCatalog.Job Job,
        GatherableCatalog.LevelBand Band,
        ImmutableArray<PlannedEntry> Entries);

    /// <summary>1 行の計画。</summary>
    public sealed record PlannedEntry(
        uint ItemId,
        uint GatheringItemId,
        string Name,
        int Level,
        uint BaselineQuantity,  // 作成時の所持数
        uint TargetQuantity);   // 目標 = BaselineQuantity + 1

    /// <summary>入れなかった理由。</summary>
    public enum HoldReason
    {
        Unknown,         // 採取状態が Unknown
        NotTracked,      // 収集品等
        ConflictOtherList, // 他の有効リストに同じ品
        QuantityCap,     // 999999 超過
        QuantityUnknown, // 所持数を取得できていない
        FolkloreLocked,  // 伝説の採集点でしか採れず、要る伝承録を読んでいない（または確かめられない）
    }

    public sealed record HeldEntry(GatherableCatalog.Entry Entry, HoldReason Reason, string Note);

    // ------------------------------------------------------------------

    /// <summary>
    /// 計画を作る。入力は Reader の結果と、他リストに存在するアイテム ID の集合。
    /// </summary>
    public static UnvisitedPlan Build(
        ulong characterContentId,
        IEnumerable<Selection> selections,
        GatherableCatalog catalog,
        GatheringCompletionReader reader,
        IReadOnlyDictionary<uint, uint> baselineQuantities, // ItemId → 作成時の所持数（GBR と同じ参照範囲）
        IReadOnlySet<uint> itemIdsInOtherEnabledLists,
        string managementTagPrefix,
        DateTime now,
        bool ventureOnly = false) // true なら「ベンチャーで依頼できる品」だけを対象にする（利用者の設定）
    {
        var selectionSet = selections.Distinct().OrderBy(s => s.Job).ThenBy(s => s.Band.SegmentIndex).ToImmutableArray();
        var listsOut = ImmutableArray.CreateBuilder<PlannedList>();
        var held = ImmutableArray.CreateBuilder<HeldEntry>();
        var plannedIds = new HashSet<uint>();

        foreach (var sel in selectionSet)
        {
            var entries = catalog.InBand(sel.Job, sel.Band);
            var plannedEntries = ImmutableArray.CreateBuilder<PlannedEntry>();

            foreach (var e in entries)
            {
                // 設定で外した品は、保留ではなく最初から対象外（件数にも数えない。Summarize と同じ扱い）
                if (ventureOnly && !e.VentureRequestable)
                    continue;

                var state = reader.Query(e);
                if (state == GatheringCompletionReader.State.NotTracked)
                {
                    held.Add(new HeldEntry(e, HoldReason.NotTracked, "収集品など、採取手帳で追跡されない品です"));
                    continue;
                }
                if (state == GatheringCompletionReader.State.Unknown)
                {
                    held.Add(new HeldEntry(e, HoldReason.Unknown, "採取履歴が読めませんでした"));
                    continue;
                }
                if (state == GatheringCompletionReader.State.Gathered)
                    continue; // 採取済みはそもそもリストに入れない

                // ここから Ungathered
                // 伝承録が足りない品を入れると、GBR は採集点の旗を待ったまま止まる（GatheringCompletionReader.FolkloreOk）
                var folklore = reader.FolkloreOk(e);
                if (folklore != true)
                {
                    held.Add(new HeldEntry(e, HoldReason.FolkloreLocked, folklore is null
                        ? "伝承録を読んだか確かめられません。入れると GBR が止まることがあるので登録しません"
                        : "伝説の採集点でしか採れず、要る伝承録を読んでいません（入れると GBR が採集点の前で待ち続けます）"));
                    continue;
                }

                if (itemIdsInOtherEnabledLists.Contains(e.ItemId))
                {
                    held.Add(new HeldEntry(e, HoldReason.ConflictOtherList, "ほかの有効リストに同じ品があります。保留します"));
                    continue;
                }

                if (!baselineQuantities.TryGetValue(e.ItemId, out var baseline))
                {
                    held.Add(new HeldEntry(e, HoldReason.QuantityUnknown, "所持数が不明です。0 個としては登録しません"));
                    continue;
                }
                // 999999 超過は保留（AutoGatherList.NormalizeQuantity の上限）
                if (baseline >= 999999)
                {
                    held.Add(new HeldEntry(e, HoldReason.QuantityCap, $"所持 {baseline} 個が上限 999999 以上です"));
                    continue;
                }

                var target = checked(baseline + 1);
                // 採掘・園芸の両方で採れる品は、目標が合算されないよう1リストだけに登録する。
                if (!plannedIds.Add(e.ItemId)) continue;
                plannedEntries.Add(new PlannedEntry(e.ItemId, e.GatheringItemId, e.Name, e.Level, baseline, target));
            }

            if (plannedEntries.Count == 0)
                continue;

            var listName = FormatListName(managementTagPrefix, sel.Job, sel.Band);
            listsOut.Add(new PlannedList(listName, sel.Job, sel.Band, plannedEntries.ToImmutable()));
        }

        return new UnvisitedPlan(
            Guid.NewGuid(),
            characterContentId,
            now,
            selectionSet,
            listsOut.ToImmutable(),
            held.ToImmutable());
    }

    /// <summary>リスト名の規約：GBRHelper_<職>_Lv1-10_未採取 など。</summary>
    public static string FormatListName(string prefix, GatherableCatalog.Job job, GatherableCatalog.LevelBand band)
    {
        var jobLabel = job switch
        {
            GatherableCatalog.Job.Miner => "鉱",
            GatherableCatalog.Job.Botanist => "園",
            _ => "?",
        };
        return $"{prefix}_{jobLabel}_{band.Label}_未採取";
    }

    /// <summary>区分（1 セル）ごとの状態サマリ。UI 用。ventureOnly の意味は <see cref="Build"/> と同じ。</summary>
    public static CellSummary Summarize(
        GatherableCatalog catalog,
        GatheringCompletionReader reader,
        GatherableCatalog.Job job,
        GatherableCatalog.LevelBand band,
        bool ventureOnly = false)
    {
        var entries = catalog.InBand(job, band);
        var ungathered = 0;
        var unknown = 0;
        var gathered = 0;
        var notTracked = 0;
        var excluded = 0;
        var folkloreLocked = 0;

        foreach (var e in entries)
        {
            if (ventureOnly && !e.VentureRequestable)
            {
                excluded++;
                continue;
            }

            var s = reader.Query(e);
            switch (s)
            {
                // 伝承録が足りない未採取品は登録できないので、ボタンの件数（Ungathered）には数えない
                case GatheringCompletionReader.State.Ungathered when reader.FolkloreOk(e) != true: folkloreLocked++; break;
                case GatheringCompletionReader.State.Ungathered: ungathered++; break;
                case GatheringCompletionReader.State.Gathered: gathered++; break;
                case GatheringCompletionReader.State.Unknown: unknown++; break;
                case GatheringCompletionReader.State.NotTracked: notTracked++; break;
            }
        }

        return new CellSummary(entries.Length - excluded, ungathered, unknown, gathered, notTracked, excluded, folkloreLocked);
    }

    /// <summary>
    /// Total は絞り込み後の件数。Ungathered は「押せば登録できる」未採取の件数（伝承録が足りない品を除く）。
    /// ExcludedNotVenture は「ベンチャーで依頼できる品だけ」の設定で外した件数（設定が OFF なら常に 0）。
    /// FolkloreLocked は未採取だが伝承録が足りない（または確かめられない）件数。
    /// </summary>
    public sealed record CellSummary(int Total, int Ungathered, int Unknown, int Gathered, int NotTracked,
        int ExcludedNotVenture = 0, int FolkloreLocked = 0);

    /// <summary>
    /// 1 つの Lv 帯のリストを作り直すときに「競合」とみなす品（＝ほかの有効リストに入っている品）を求める。
    ///
    /// GBR は有効なリストどうしで同じ品の目標数を合算する（AutoGatherListsManager.SetActiveItems の Sum）。
    /// 同じ品が 2 本の有効リストに入ると「所持＋1」が「所持×2＋2」になり、余分に採る。そのため、
    /// - 無効なリストは数えない（GBR も合算しない）
    /// - ほかの Lv 帯・職の Helper のリストも数える（両職で採れる品が採掘と園芸の両方に入らないように）
    /// - 作り直す本人のリスト（同じ名前かつ同じ管理タグ）だけは数えない（置き換えるので）
    /// 同じ名前でも管理タグが無いリストは利用者のものなので、競合として数える。
    /// </summary>
    public static IReadOnlySet<uint> ConflictingItemIds(
        IEnumerable<Ipc.GbrAutoGatherListAccess.ListSummary> lists,
        string ownListName,
        string managementTag)
    {
        var set = new HashSet<uint>();
        foreach (var list in lists)
        {
            if (!list.Enabled)
                continue;
            if (list.Name == ownListName && list.Description.Contains(managementTag, StringComparison.Ordinal))
                continue;
            foreach (var (id, _) in list.Entries)
                set.Add(id);
        }

        return set;
    }
}
