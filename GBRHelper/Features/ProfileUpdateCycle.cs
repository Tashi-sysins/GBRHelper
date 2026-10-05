using System;
using System.Collections.Generic;
using System.Linq;
using GBRHelper.Ipc;

namespace GBRHelper.Features;

/// <summary>準備失敗と書込み待ちを区別する。停止・書込みは準備成功後だけ。</summary>
public sealed class ProfileUpdateCycle
{
    public enum Step { Wait, Stop, Write }
    public sealed record Result(Step Action, string Error = "");
    public bool IsWriting { get; private set; }
    public static Step Decide(bool ready, bool busy, bool safe, bool? enabled, bool allowStop)
    {
        if (!ready || busy || !safe || enabled is null) return Step.Wait;
        if (enabled == true) return allowStop ? Step.Stop : Step.Wait;
        return Step.Write;
    }
    public Result Run(bool cleanup, bool busy, bool allowStop, Func<bool> prepare,
        Func<bool> safe, Func<bool?> enabled, Action stop, Action write)
    {
        if (busy) return new(Step.Wait);
        bool ready;
        try { ready = cleanup || prepare(); }
        catch (Exception ex) { return new(Step.Wait, ex.GetBaseException().Message); }
        // 読取りに失敗した場合、停止用APIや境界の読取りにも進まない。
        if (!ready) return new(Step.Wait);
        var step = Decide(ready, busy, safe(), enabled(), allowStop || cleanup);
        if (step == Step.Stop) stop();
        if (step == Step.Write)
        {
            IsWriting = true;
            try { write(); } finally { IsWriting = false; }
        }
        return new(step);
    }
    /// <summary>
    /// 消すリスト：その回に書き直す機能（scope）のうち反映した帯に無いものと、別のキャラクターのリスト（機能によらず。その人の品を採ってしまうため）。
    /// 要望：解放採取と全素材の補充の「Auto-Gatherに追加」は別物。押していない機能のリストは消さない。
    /// </summary>
    public static GatherProfiles.Owned[] Obsolete(IEnumerable<GatherProfiles.Owned> owned, ulong cid,
        IReadOnlyDictionary<ulong, GatherProfile> profiles, bool migrated, IReadOnlySet<GatherProfileKind> scope)
        => owned.Where(x => MustRemove(x, cid, profiles, migrated)
            && (scope.Contains(x.Kind) || (x.Character != 0 && x.Character != cid))).ToArray();

    /// <summary>
    /// 書き直しの比べる相手（SamePlans）に渡す GBR のリスト：こちらのリストは、その回に書き直す機能のものだけ（ほかの機能のリストは比べない＝触らない）。
    /// 利用者のリストは SamePlans が見ないのでそのまま渡す。
    /// </summary>
    public static GbrAutoGatherListAccess.ListSummary[] InScope(IEnumerable<GbrAutoGatherListAccess.ListSummary> snapshot,
        IReadOnlySet<GatherProfileKind> scope)
        => snapshot.Where(x => GatherProfiles.Identify(x) is not { } o || scope.Contains(o.Kind)).ToArray();

    /// <summary>
    /// 作るリストに入れない品（ほかの有効なリストにある品。GBR は有効なリストどうしで同じ品の目標数を足すため）：
    /// 利用者の有効なリストと、その回に書き直さない機能の、こちらの有効なリスト。
    /// （書き直す機能のこちらのリストは作り直すので入れない。書き直す機能どうしは、解放採取 → 全素材の補充の順に作って避ける）
    /// </summary>
    public static HashSet<uint> ConflictItems(IEnumerable<GbrAutoGatherListAccess.ListSummary> snapshot,
        IReadOnlySet<GatherProfileKind> scope)
        => snapshot.Where(x => x.Enabled && (GatherProfiles.Identify(x) is not { } o || !scope.Contains(o.Kind)))
            .SelectMany(x => x.Entries).Select(e => e.ItemId).ToHashSet();

    public static bool MustRemove(GatherProfiles.Owned list, ulong cid,
        IReadOnlyDictionary<ulong, GatherProfile> profiles, bool migrated)
        => (list.Character != 0 && list.Character != cid)
            || ((!list.Legacy || migrated) && !GatherProfiles.Effective(profiles, cid, list.Kind).Contains(list.Key));
    public static bool ReuseOnLoad(IReadOnlyDictionary<ulong, GatherProfile> profiles, ulong cid,
        IReadOnlyList<GbrAutoGatherListAccess.ListSummary> snapshot)
    {
        if (cid == 0) return false;
        var owned = snapshot.Select(GatherProfiles.Identify).OfType<GatherProfiles.Owned>().ToArray();
        if (owned.Any(x => x.Legacy || x.Character != cid)) return false;
        // 「リテイナーを数えるか」が今の設定と違うリスト（例：999 固定の頃に作った補充のリスト）はそのまま使わず、作り直す。
        if (snapshot.Any(x => GatherProfiles.Identify(x) is { } o && x.UsesRetainerInventory != UsesRetainers(o.Kind))) return false;
        // 名前がいまの形と違うリスト（前の版のキャラクターの番号入りの名前・数を変えた補充）は、そのまま使わず作り直す。
        profiles.TryGetValue(cid, out var profile);
        if (owned.Any(x => x.Name != GatherProfiles.ListName(x.Kind, x.Key, profile))) return false;
        var desired = Enum.GetValues<GatherProfileKind>()
            .SelectMany(kind => GatherProfiles.Effective(profiles, cid, kind).Select(key => (kind, key))).ToHashSet();
        return owned.Length == desired.Count && desired.SetEquals(owned.Select(x => (x.Kind, x.Key)));
    }
    public static bool SamePlans(IReadOnlyList<ProfileListSync.Plan> plans,
        IReadOnlyList<GbrAutoGatherListAccess.ListSummary> snapshot)
    {
        var owned = snapshot.Where(x => GatherProfiles.Identify(x) is not null).ToArray();
        return owned.Length == plans.Count && plans.All(p => owned.Any(x => x.Name == p.GbrName && x.Description == p.Tag
            && x.Enabled == p.Enabled && x.AllItemsEnabled && !x.Fallback && x.UsesRetainerInventory == p.UsesRetainerInventory
            && x.Entries.SequenceEqual(p.Entries)));
    }

    /// <summary>その種類のリストで、GBR に所持数へリテイナーの在庫も足させるか。全素材の補充だけ GatherProfiles.StockUsesRetainers に従う。</summary>
    public static bool UsesRetainers(GatherProfileKind kind)
        => kind != GatherProfileKind.Stock || GatherProfiles.StockUsesRetainers;
    public static bool AllowRelayTick(bool reducing, bool writing) => !reducing && !writing;

    /// <summary>
    /// GBR の採集中に、GBR を止めずに待つか（2026-10-05）。
    /// 「Auto-Gatherに追加」（allowStop = false）なら止めない（ボタンは自動採集中は押せないが、押した直後に GBR が動き出したときなど）。
    /// キャラクターを替えたとき（allowStop = true）と、別のキャラクターのリストが残っているとき（その人の品を採ってしまう）は、
    /// 従来どおり採集の切れ目で止めて書く。
    /// </summary>
    public static bool WaitWhileGathering(bool? enabled, bool allowStop, bool otherCharacterLists)
        => enabled == true && !allowStop && !otherCharacterLists;

    public static bool RefreshMayRequest(bool? enabled) => enabled == false;
}

public sealed class ProfileRetrySchedule
{
    private int failures;
    public TimeSpan Failed() => TimeSpan.FromSeconds(Math.Min(60, 2 * Math.Pow(2, Math.Min(++failures, 5))));
    public void Reset() => failures = 0;
}
