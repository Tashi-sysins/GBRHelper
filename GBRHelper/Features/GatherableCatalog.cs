using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace GBRHelper.Features;

/// <summary>
/// 採掘・園芸の採取物の目録。「再解析」のたびに作り直す。
///
/// 【入力】
///   1. GBR の GameData.Gatherables（アイテム Lv・GatheringType・GatheringId）。
///      いまの画面（ベンチャー依頼品の解放採取）は GBR の Gatherables タブの Log 列と同じ範囲を
///      出すため、こちらを全件使う（<see cref="BuildGatherables"/>）。
///   2. Lumina の RetainerTaskNormal（ベンチャー行）→ Item.RowId と GatheringLog.RowId。
///      「ベンチャーで依頼できる品か」の印に使う。
///      ※ RetainerTaskNormal.GatheringLog は GatheringItem への参照（sheet 名から推測せず
///         GatheringItem の RowId として扱う。指示書 §ソース確認結果）
///      旧方式（ベンチャー行だけで目録を作る <see cref="Build"/>）は検証のために残している。
///
/// 【Core/Effects 分離（GCAutoRanker 風）】
///   このクラスは「渡された行を分類する」純粋ロジックで、Lumina も GBR も直接触らない。
///   ゲーム依存のデータ取得は呼び出し側（Plugin / Feature）が行い、分類ロジックだけここでテストする。
///   指示書：「Core/Effects分離を参考に、ゲームのネイティブ関数をゲーム外テストから直接呼ばず」
///
/// 【分類方針】
///   職：Mining / Quarrying / Miner → 採掘、Logging / Harvesting / Botanist → 園芸
///   Lv帯：素材の採取 Lv を 10 刻みで区切る（Lv1-10, 11-20, ..., 91-100）
///   Lv0・Lv>100・Unknown は Diagnostics へ回し、未採取カウントに混ぜない
/// </summary>
public sealed class GatherableCatalog
{
    /// <summary>職の区分。採掘と園芸のみ扱う（釣り・刺突漁は本機能では対象外）。</summary>
    public enum Job
    {
        Miner,     // 採掘（Mining / Quarrying / Miner）
        Botanist,  // 園芸（Logging / Harvesting / Botanist）
    }

    /// <summary>Lv 帯（1-10, 11-20, ..., 91-100）。SegmentIndex 0〜9。</summary>
    public readonly struct LevelBand
    {
        public int SegmentIndex { get; }
        public int MinLevel => SegmentIndex * 10 + 1;
        public int MaxLevel => SegmentIndex * 10 + 10;
        public string Label => $"Lv{MinLevel}-{MaxLevel}";

        public LevelBand(int segmentIndex)
        {
            if (segmentIndex is < 0 or > 9)
                throw new ArgumentOutOfRangeException(nameof(segmentIndex), segmentIndex, "0〜9 のみ");
            SegmentIndex = segmentIndex;
        }

        /// <summary>Lv から帯を求める。範囲外（0 や 101 以上）は null。</summary>
        public static LevelBand? FromLevel(int level)
        {
            if (level is < 1 or > 100)
                return null;
            return new LevelBand((level - 1) / 10);
        }
    }

    /// <summary>1 件の採取物。</summary>
    public sealed record Entry(
        uint ItemId,
        uint GatheringItemId,
        string Name,
        int Level,
        Job Job,
        /// <summary>IsGatheringItemGathered では追跡できない（収集品等）。元資料 §LogState.NotTracked 相当。</summary>
        bool NotTracked,
        /// <summary>ベンチャー（RetainerTaskNormal の採掘・園芸の行）で依頼できる品か。復興用・クラスター・伝説の品などは false。</summary>
        bool VentureRequestable = false,
        /// <summary>
        /// 採るのに要る伝承録の品番（すべて読む必要がある）。null・空＝要らない（伝承録の要らない採集点がある）。
        /// GBR の GatheringNode.FolkloreId（＝GatheringSubCategory.Item）から作る。
        /// </summary>
        IReadOnlyList<uint>? FolkloreBooks = null,
        bool Collectable = false, bool TreasureMap = false, MaterialNodeKinds NodeKinds = MaterialNodeKinds.None,
        /// <summary>採集点が出るエオルゼア時間（0〜23 時のビット。全採集点の合わせ）。0 は不明。</summary>
        uint UptimeHours = 0
    );

    /// <summary>構築時の診断情報。「対象 0 件」になる原因を追えるようにする。</summary>
    public sealed record Diagnostics(
        int InputTaskRows,
        int InputItemRows,
        int MatchedGbrItems,
        int SkippedUnknownJob,
        int SkippedLevelOutOfRange,
        int SkippedGatheringIdInvalid,
        int SkippedNotInGbr,
        IReadOnlyList<string> Warnings
    );

    public ImmutableArray<Entry> Entries { get; }
    public Diagnostics Diag { get; }

    /// <summary>
    /// <see cref="Entry.VentureRequestable"/> が当てになるか（ベンチャーの行を読めたか）。
    /// 読めなかったときは全件 false になっているので、「ベンチャー可の品だけ」の絞り込みに使わない。
    /// </summary>
    public bool VentureInfoAvailable { get; }

    private readonly ImmutableDictionary<(Job Job, int Band), ImmutableArray<Entry>> buckets;

    private GatherableCatalog(ImmutableArray<Entry> entries, Diagnostics diag, bool ventureInfoAvailable)
    {
        this.Entries = entries;
        this.Diag = diag;
        this.VentureInfoAvailable = ventureInfoAvailable;
        this.buckets = entries
            .Where(e => LevelBand.FromLevel(e.Level) is not null)
            .GroupBy(e => (e.Job, Band: LevelBand.FromLevel(e.Level)!.Value.SegmentIndex))
            .ToImmutableDictionary(g => g.Key, g => g.ToImmutableArray());
    }

    /// <summary>指定の 職・Lv帯 に含まれるエントリ。</summary>
    public ImmutableArray<Entry> InBand(Job job, LevelBand band)
        => this.buckets.TryGetValue((job, band.SegmentIndex), out var v) ? v : ImmutableArray<Entry>.Empty;

    /// <summary>
    /// Gatherables 全体を Lv・職で分類する。ベンチャー対象による絞り込みは行わない
    /// （GBR の Gatherables タブの Log 列と同じ範囲を出すため）。
    /// ventureItemIds を渡すと、各品に「ベンチャーで依頼できるか」の印を付ける（絞り込むかは呼び出し側が決める）。
    /// null は「ベンチャーの行を読めなかった」で、印は全件 false・VentureInfoAvailable も false になる。
    /// </summary>
    public static GatherableCatalog BuildGatherables(
        IReadOnlyDictionary<uint, GbrGatherableView> gatherables,
        IReadOnlySet<uint>? ventureItemIds = null)
    {
        var entries = new List<Entry>();
        var unknownJobs = 0;
        var invalidLevels = 0;
        var warnings = new List<string>();
        foreach (var g in gatherables.Values.OrderBy(g => g.Level).ThenBy(g => g.ItemId))
        {
            if (LevelBand.FromLevel(g.Level) is null)
            {
                invalidLevels++;
                continue;
            }
            var directJob = ClassifyJob(g.GatheringType);
            var jobs = directJob is { } job ? new[] { job }
                : g.GatheringType == GbrGatheringType.Multiple
                    ? (g.NodeGatheringTypes ?? []).Select(ClassifyJob).Where(j => j.HasValue).Select(j => j!.Value).Distinct().ToArray()
                    : Array.Empty<Job>();
            if (jobs.Length == 0)
            {
                unknownJobs++;
                warnings.Add($"Item {g.ItemId}: 採掘・園芸の職を判定できません");
                continue;
            }
            var venture = ventureItemIds?.Contains(g.ItemId) ?? false;
            foreach (var classifiedJob in jobs)
                entries.Add(new Entry(g.ItemId, g.GatheringItemId, g.Name, g.Level, classifiedJob, g.IsCollectableOrNotTracked, venture, g.FolkloreBooks, g.Collectable, g.TreasureMap, g.NodeKinds, g.UptimeHours));
        }
        // ID 不正は Reader が Unknown にする。未採取（×）には含めない。
        return new GatherableCatalog(entries.ToImmutableArray(),
            new Diagnostics(0, gatherables.Count, entries.Count, unknownJobs, invalidLevels,
                entries.Count(e => !e.NotTracked && (e.GatheringItemId == 0 || e.GatheringItemId > ushort.MaxValue)), 0, warnings),
            ventureItemIds is not null);
    }

    /// <summary>純粋ロジックで目録を作る。Lumina・GBR には触らない。</summary>
    public static GatherableCatalog Build(
        IEnumerable<RetainerTaskRow> taskRows,
        IReadOnlyDictionary<uint, GbrGatherableView> gbrGatherables)
    {
        var warnings = new List<string>();

        var inputTaskRows = 0;
        var inputItemRows = 0;
        var matchedGbrItems = 0;
        var skippedUnknownJob = 0;
        var skippedLevelOutOfRange = 0;
        var skippedGatheringIdInvalid = 0;
        var skippedNotInGbr = 0;

        var map = new Dictionary<uint, Entry>();

        foreach (var row in taskRows)
        {
            inputTaskRows++;
            if (row.ItemId == 0 || row.GatheringLogRowId == 0)
                continue;
            inputItemRows++;

            if (!gbrGatherables.TryGetValue(row.ItemId, out var g))
            {
                skippedNotInGbr++;
                continue;
            }
            matchedGbrItems++;

            // 職の分類
            var job = ClassifyJob(g.GatheringType);
            if (job is null)
            {
                skippedUnknownJob++;
                continue;
            }

            // Lv の範囲
            var band = LevelBand.FromLevel(g.Level);
            if (band is null)
            {
                skippedLevelOutOfRange++;
                continue;
            }

            // GatheringItem の RowId 検証（QuestManager に渡す ushort の制約）
            // RetainerTaskNormal.GatheringLog.RowId と GBR の GatheringId の両方を見て、
            // 一致しない場合は診断対象にする（指示書：どちらかを無条件に採用しない）。
            var effectiveGid = g.GatheringItemId;
            if (effectiveGid == 0)
                effectiveGid = row.GatheringLogRowId;
            else if (row.GatheringLogRowId != 0 && effectiveGid != row.GatheringLogRowId)
                warnings.Add($"Item {row.ItemId}: GatheringLog.RowId={row.GatheringLogRowId} と GBR.GatheringId={g.GatheringItemId} が一致しません（GBR 側を採用）");

            if (effectiveGid == 0 || effectiveGid > ushort.MaxValue)
            {
                skippedGatheringIdInvalid++;
                continue;
            }

            // 同じ ItemId の重複はまとめる（RetainerTaskNormal には同一アイテムが複数行で現れる）。
            if (!map.ContainsKey(row.ItemId))
            {
                map[row.ItemId] = new Entry(
                    row.ItemId,
                    effectiveGid,
                    g.Name,
                    g.Level,
                    job.Value,
                    g.IsCollectableOrNotTracked,
                    VentureRequestable: true, // ベンチャーの行から作るので必ず依頼できる品
                    FolkloreBooks: g.FolkloreBooks
                );
            }
        }

        var diag = new Diagnostics(
            inputTaskRows,
            inputItemRows,
            matchedGbrItems,
            skippedUnknownJob,
            skippedLevelOutOfRange,
            skippedGatheringIdInvalid,
            skippedNotInGbr,
            warnings
        );

        return new GatherableCatalog(map.Values.ToImmutableArray(), diag, ventureInfoAvailable: true);
    }

    /// <summary>
    /// 品の採集点ごとの伝承録の品番（GatheringNode.FolkloreId。0＝要らない）から、その品に要る伝承録を決める。
    /// 伝承録の要らない採集点が 1 つでもあれば null（要らない）。採集点が無いときも null。
    /// すべての採集点で要るときは、その品番（重複なし）。GBR の Folklore 列と同じ考え方（Interface.ItemTab.cs:736-739）。
    /// </summary>
    public static IReadOnlyList<uint>? RequiredFolkloreBooks(IReadOnlyCollection<uint> nodeFolkloreIds)
    {
        if (nodeFolkloreIds.Count == 0 || nodeFolkloreIds.Contains(0u))
            return null;
        return nodeFolkloreIds.Distinct().ToArray();
    }

    /// <summary>
    /// GBR の GatheringType を、採掘 / 園芸の 2 択に落とす。
    /// Mining / Quarrying / Miner → 採掘、Logging / Harvesting / Botanist → 園芸、他は null（除外）。
    /// </summary>
    public static Job? ClassifyJob(GbrGatheringType type) => type switch
    {
        GbrGatheringType.Mining or GbrGatheringType.Quarrying or GbrGatheringType.Miner => Job.Miner,
        GbrGatheringType.Logging or GbrGatheringType.Harvesting or GbrGatheringType.Botanist => Job.Botanist,
        // Multiple は本来「複数の方法で採れる」。関連ノードから解決できない限り除外する（指示書）。
        _ => null,
    };

    /// <summary>
    /// GBR の GatheringType に対応する値。GBR のアセンブリに依存しないよう、
    /// 数値だけここで持つ（元定義：GatherBuddy.Enums.GatheringType）。
    /// </summary>
    public enum GbrGatheringType : byte
    {
        Mining = 0,
        Quarrying = 1,
        Logging = 2,
        Harvesting = 3,
        Spearfishing = 4,
        Botanist = 5,
        Miner = 6,
        Fisher = 7,
        Multiple = 8,
        Unknown = byte.MaxValue,
    }
}

/// <summary>
/// RetainerTaskNormal の 1 行から取り出した必要項目。
/// Lumina への依存をカタログから切るための中間型。
/// GatheringLogRowId は RetainerTaskNormal.GatheringLog.RowId（= GatheringItem の RowId）。
/// </summary>
public sealed record RetainerTaskRow(uint TaskRowId, uint ItemId, uint GatheringLogRowId);

/// <summary>
/// GBR の Gatherable から取り出した必要項目。
/// GBR の実型（Gatherable）に依存しないための中間型。
/// </summary>
public sealed record GbrGatherableView(
    uint ItemId,
    string Name,
    int Level,
    uint GatheringItemId,
    GatherableCatalog.GbrGatheringType GatheringType,
    bool IsCollectableOrNotTracked,
    IReadOnlyList<GatherableCatalog.GbrGatheringType>? NodeGatheringTypes = null,
    IReadOnlyList<uint>? FolkloreBooks = null,
    bool Collectable = false, bool TreasureMap = false, MaterialNodeKinds NodeKinds = MaterialNodeKinds.None, // 要る伝承録の品番。null・空＝要らない
    uint UptimeHours = 0); // 採集点が出るエオルゼア時間（0〜23 時のビット）。0 は不明
