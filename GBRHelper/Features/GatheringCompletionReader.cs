using System;
using System.Collections.Generic;

namespace GBRHelper.Features;

/// <summary>
/// 本人の採取済み判定。QuestManager.IsGatheringItemGathered を使う。
///
/// 【API】
///   FFXIVClientStructs.FFXIV.Client.Game.QuestManager.IsGatheringItemGathered(ushort gatheringItemId)
///   引数は Item.RowId ではなく GatheringItem.RowId（指示書 §ソース確認結果）。
///
/// 【分離】
///   このクラスはキャッシュと「状態の変換ルール」だけを担う純粋ロジック。
///   実際の API 呼び出しは <see cref="ICompletionEffects"/> に委ねる（GCAutoRanker 風）。
///   ゲーム外の検証では ICompletionEffects を偽物に差し替える。
///
/// 【指示書で特に求められた挙動】
///   1. ContentId 単位でキャッシュを分離。ログアウトやキャラ変更で失効させる。
///   2. GatheringItemId == 0 または > ushort.MaxValue のときは API を呼ばず Unknown。
///   3. 判定不能のときは Unknown。全件未採取・全件完了のどちらにも倒さない。
///   4. 所持数の増加だけで本人採取済みと判定しない（このクラスは所持数を見ない）。
///   5. GBR の NotTracked（収集品・地図など）は Ungathered に変えない。NotTracked のまま。
/// </summary>
public sealed class GatheringCompletionReader
{
    public enum State
    {
        /// <summary>採取手帳の API が「採取済み」と答えた。</summary>
        Gathered,
        /// <summary>採取手帳の API が「未採取」と答えた。</summary>
        Ungathered,
        /// <summary>読めなかった・まだ読んでいない・ログイン前・API が落ちた。</summary>
        Unknown,
        /// <summary>この採取物は手帳で追跡されない（収集品・地図等）。本機能では未採取リストに含めない。</summary>
        NotTracked,
    }

    private readonly ICompletionEffects effects;
    private readonly Dictionary<ulong, Dictionary<uint, State>> cacheByContentId = new();
    private readonly Dictionary<ulong, Dictionary<uint, bool>> folkloreByContentId = new();

    /// <summary>
    /// 伝承録の条件を満たしているか。true＝要らない、または必要な伝承録をすべて読んでいる。
    /// false＝読んでいない伝承録がある。null＝確かめられない（ログイン前・読込み前など）。
    ///
    /// 【なぜ見るか】GBR の自動採集は伝承録を読んだかを見ない。伝説の採集点の品をリストに入れると、
    /// 伝承録が無い人は地図の旗が出るまでログも時間切れも無く待ち続ける（2026-10-02 実機で確定。
    /// AutoGather.cs:2203-2209）。蒼天の伝説は ET 24 時間を隙間なく覆うので、通常の品にも順番が来なくなる。
    /// 伝承録が 2 冊要る品（2026-10-05 時点で 2 品）は、GBR が未読側の採集点を選ぶと止まるので「すべて」を求める。
    /// </summary>
    public bool? FolkloreOk(GatherableCatalog.Entry entry)
    {
        if (entry.FolkloreBooks is not { Count: > 0 } books)
            return true;

        var contentId = this.effects.GetLocalContentId();
        if (contentId is not { } cid || cid == 0)
            return null;

        if (!this.folkloreByContentId.TryGetValue(cid, out var map))
            this.folkloreByContentId[cid] = map = new Dictionary<uint, bool>();

        var unknown = false;
        foreach (var book in books)
        {
            if (!map.TryGetValue(book, out var read))
            {
                var r = this.effects.IsFolkloreBookRead(book);
                if (r is null)
                {
                    unknown = true; // 読めないものはキャッシュしない（次に再試行）
                    continue;
                }

                map[book] = read = r.Value;
            }

            if (!read)
                return false;
        }

        return unknown ? null : true;
    }

    public GatheringCompletionReader(ICompletionEffects effects)
    {
        this.effects = effects;
    }

    /// <summary>
    /// 指定のエントリの状態を調べる。キャッシュがあればそれを返し、無ければ API を 1 回呼ぶ。
    /// </summary>
    public State Query(GatherableCatalog.Entry entry)
    {
        // NotTracked は API を呼ばない（指示書：GBR の NotTracked 判定を維持する）。
        if (entry.NotTracked)
            return State.NotTracked;

        // GatheringItemId の正当性チェック（API に不正な ID を渡さない）。
        if (entry.GatheringItemId == 0 || entry.GatheringItemId > ushort.MaxValue)
            return State.Unknown;

        // ログインしていなければ Unknown。
        var contentId = this.effects.GetLocalContentId();
        if (contentId is not { } cid || cid == 0)
            return State.Unknown;

        var map = this.GetOrCreateMap(cid);
        if (map.TryGetValue(entry.GatheringItemId, out var cached))
            return cached;

        var result = this.effects.CallIsGatheringItemGathered((ushort)entry.GatheringItemId);
        State state;
        if (result is null)
            state = State.Unknown;
        else
            state = result.Value ? State.Gathered : State.Ungathered;

        // Unknown はキャッシュしない（次フレームで再試行できるようにする）。
        if (state != State.Unknown)
            map[entry.GatheringItemId] = state;

        return state;
    }

    /// <summary>
    /// 複数のエントリをまとめて調べる。順序は入力と同じ。
    /// </summary>
    public IReadOnlyList<State> QueryMany(IReadOnlyList<GatherableCatalog.Entry> entries)
    {
        var result = new State[entries.Count];
        for (var i = 0; i < entries.Count; i++)
            result[i] = this.Query(entries[i]);
        return result;
    }

    /// <summary>特定キャラのキャッシュを捨てる（キャラ切替時やログアウト時に呼ぶ）。</summary>
    public void InvalidateCharacter(ulong contentId)
    {
        if (this.cacheByContentId.TryGetValue(contentId, out var map))
            map.Clear();
        if (this.folkloreByContentId.TryGetValue(contentId, out var folklore))
            folklore.Clear();
    }

    /// <summary>全キャラのキャッシュを捨てる（GBR 再ロード時など）。</summary>
    public void InvalidateAll()
    {
        this.cacheByContentId.Clear();
        this.folkloreByContentId.Clear();
    }

    private Dictionary<uint, State> GetOrCreateMap(ulong contentId)
    {
        if (!this.cacheByContentId.TryGetValue(contentId, out var map))
        {
            map = new Dictionary<uint, State>();
            this.cacheByContentId[contentId] = map;
        }
        return map;
    }
}

/// <summary>
/// 採取済み判定のゲーム依存部分を差し替えられるようにする窓口。
/// 検証ではこのインタフェースの偽物を使う。
/// </summary>
public interface ICompletionEffects
{
    /// <summary>いまログインしているキャラの ContentId。ログイン前なら null。</summary>
    ulong? GetLocalContentId();

    /// <summary>
    /// QuestManager.IsGatheringItemGathered を呼ぶ。
    /// Framework スレッド上かどうかの判定も実装側で行う。
    /// 呼べなければ null を返す（Unknown 扱いになる）。
    /// </summary>
    bool? CallIsGatheringItemGathered(ushort gatheringItemId);

    /// <summary>
    /// 伝承録（品番＝GatheringSubCategory.Item）を本人が読んだか。確かめられなければ null。
    /// </summary>
    bool? IsFolkloreBookRead(uint bookItemId);
}
