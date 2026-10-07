using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using GBRHelper.Ipc;
using GBRHelper.Ui;
using Lumina.Excel.Sheets;

namespace GBRHelper.Features;

/// <summary>
/// ヤンサの山越え。GBR の自動採集がヤンサで山の向こう側へ向かうとき、山に詰まる前に、
/// 向こう側のエーテライト（烈士庵・ナマイ村）へテレポする。判断は YanxiaRoute（なぜ要るか・条件もそちら）。
///
/// 9 月に作って、要望で外した「ヤンサの迂回」を、次の点を直して戻したもの：
///   ・エーテライトの位置は、見えたときに覚えるのをやめ、ゲームデータの地図の印から引く（一度も見ていないと判断できなかった）
///   ・行き先がエーテライトの 150m 以内のときだけ、をやめ、経路が山と川の帯を通るかで決める（南の採集点はナマイ村から離れている。YanxiaRoute）
///   ・詰まりを見つけたときの飛び先を烈士庵に決め打ちせず、行き先に近い方にする
///   ・GBR を止める・戻すのは、ベンチャー回収の「自分の操作」の数え方を通す（前はベンチャーの見張りが止まって始め直していた）
///   ・詰まりの場所に今回の山の斜面を足した
/// 【流れ】GBR を OFF（自分の操作として）→ vnavmesh の移動を止める → Lifestream でテレポ → エーテライトの 150m 以内に着いたら GBR を ON。
///   GBR は ON になると、いまの場所から行き先へ経路を求め直す（同じ側なので山を越えない）。
/// 【手を出さないとき】GBR の自動採集が OFF・ヤンサの外・ほかの自動処理中（ベンチャー回収で宿屋へ向かう〜戻す・宿屋の検証・
///   霊砂の精選・リストの書き直し。blockReason）・採集中や詠唱中など・テレポしてから 30 秒。
/// </summary>
public sealed class YanxiaShortcut(Configuration config, RunLog log, VnavmeshIpc navmesh, LifestreamIpc lifestream,
    GatherBuddyIpc gbr, Func<Func<bool>, bool> issueGbrChange, Func<string?> blockReason) : IFeature
{
    /// <summary>ヤンサのエリア番号（TerritoryType 614。9 月の版と同じ）。エーテライトと地図はこの番号からゲームデータで引く。</summary>
    public const uint YanxiaTerritory = 614;

    /// <summary>着いたとみなす、エーテライトからの距離（m。9 月の版と同じ）。</summary>
    private const float ArrivedRange = YanxiaRoute.NearRange;

    private static readonly TimeSpan CheckCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TeleportTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);

    public string Name => "ヤンサの山越え";

    public string Description => "GBR の自動採集がヤンサで山の向こう側へ向かうとき、山に詰まる前に、向こう側のエーテライト（烈士庵・ナマイ村）へテレポします。";

    /// <summary>ベンチャー回収（0）のすぐ下。</summary>
    public int SortOrder => 10;

    /// <summary>左ペインのチェックは出さない（「有効」は右側。GBR の日本語表示と同じ作り）。</summary>
    public bool Enabled { get => true; set { } }

    public bool HideEnableToggle => true;

    private enum Phase { Idle, Teleporting, Restoring }

    private Phase phase = Phase.Idle;
    private YanxiaRoute.Aetheryte? target;
    private DateTime phaseDeadline, teleportIssuedAt, nextTry, nextCheckAllowedAt;
    private string detail = "";

    /// <summary>いまテレポの処理中か（Plugin がほかの自動処理を待たせるのに使う）。</summary>
    public bool Running => phase != Phase.Idle;

    /// <summary>直近の判断（なぜテレポした／しなかったか。デバッグに出す。しきい値が合っているかは実際の距離を見ないと分からないため）。</summary>
    public string Diagnosis { get; private set; } = "";

    /// <summary>山越えでテレポした回数（このゲームを起動してから）。</summary>
    public int Count { get; private set; }

    public void DrawRight()
    {
        var on = config.YanxiaShortcutOn;
        if (ImGui.Checkbox("有効##yanxia", ref on))
        {
            config.YanxiaShortcutOn = on;
            config.Save();
            if (!on && phase == Phase.Teleporting) Restore("「有効」を外しました");
        }
        if (detail.Length != 0)
            ImGui.TextWrapped(detail);
    }

    public void ResetCharacter()
    {
        // キャラクターが変わったら、こちらの段取りだけ捨てる（GBR には触らない）。
        phase = Phase.Idle; target = null; detail = "";
        phaseDeadline = teleportIssuedAt = nextTry = nextCheckAllowedAt = default;
        aetherytes = null; attunedAt = default; stuck.Reset();
    }

    /// <summary>「Auto-Gatherに停止」などで利用者が止めたとき：GBR を戻さずに段取りをやめる。</summary>
    public void Cancel(string reason)
    {
        if (phase == Phase.Idle) return;
        log.Write("Yanxia", $"山越えのテレポをやめました（{reason}）");
        phase = Phase.Idle; target = null; detail = "";
        nextCheckAllowedAt = DateTime.UtcNow + CheckCooldown;
    }

    public void Tick()
    {
        switch (phase)
        {
            case Phase.Idle: TickIdle(); break;
            case Phase.Teleporting: TickTeleporting(); break;
            case Phase.Restoring: TickRestoring(); break;
        }
    }

    // ------------------------------------------------------------------
    // 見張り

    private void TickIdle()
    {
        if (!config.YanxiaShortcutOn || !Me.Available || Svc.ClientState.TerritoryType != YanxiaTerritory)
        {
            stuck.Reset();
            return;
        }
        if (DateTime.UtcNow < nextCheckAllowedAt || gbr.IsAutoGatherEnabled() != true || blockReason() is not null || IsBusyForTeleport())
        {
            stuck.Reset();
            return;
        }
        var aeths = Aetherytes();
        if (navmesh.ListWaypoints() is not { Count: > 0 } waypoints)
        {
            Diagnosis = "いま経路が立っていません";
            stuck.Reset();
            return;
        }

        // 先に経路で判断する（詰まる前に飛べる方が無駄がない）。
        var decision = YanxiaRoute.Decide(Me.Position, waypoints, aeths);
        Diagnosis = decision.Reason;
        if (decision.Target is { } to)
        {
            log.Write("Yanxia", $"{to.Name}へテレポします（{decision.Reason}）");
            BeginTeleport(to);
            return;
        }

        // 経路では拾えなかった詰まりを、詰まりの場所で動けていないことで拾う（YanxiaStuckSpots）。
        // 飛び先は行き先に近い方のエーテライト（テレポした方が遠くなっても、詰まったままよりよい）。
        var moving = navmesh.IsMoving() && !Svc.Condition[ConditionFlag.Gathering] && !Svc.Condition[ConditionFlag.ExecutingGatheringAction];
        if (stuck.Check(Me.Position, moving, DateTime.UtcNow) is { } why)
        {
            var rescue = YanxiaRoute.Toward(Me.Position, waypoints[^1], aeths, requireCloser: false);
            Diagnosis = $"{why}。{rescue.Reason}";
            if (rescue.Target is { } side)
            {
                log.Write("Yanxia", $"詰まりを見つけたので {side.Name}へテレポします（{why}）");
                BeginTeleport(side);
            }
        }
    }

    /// <summary>詰まりの場所での見張り（9 月の版の StuckSpot＋今回の山の斜面）。</summary>
    private readonly YanxiaStuckSpots stuck = new();

    // ------------------------------------------------------------------
    // テレポ

    /// <summary>GBR を止めてテレポする。GBR を動かしたままだと、GBR の移動とテレポの詠唱が取り合って詠唱が切れる（9 月の版の実機）。</summary>
    private void BeginTeleport(YanxiaRoute.Aetheryte to)
    {
        if (!issueGbrChange(() => gbr.SetAutoGatherEnabled(false)))
        {
            log.Write("Yanxia", "自動採集を止められないため、テレポをやめます");
            nextCheckAllowedAt = DateTime.UtcNow + CheckCooldown;
            return;
        }
        // GBR が積んだ経路が残っていると、テレポしたあとそこへ動き出す。
        navmesh.Stop();
        target = to;
        phase = Phase.Teleporting;
        teleportIssuedAt = nextTry = default;
        phaseDeadline = DateTime.UtcNow + TeleportTimeout;
        stuck.Pause(DateTime.UtcNow);
        detail = $"{to.Name}へテレポしています";
    }

    private void TickTeleporting()
    {
        var to = target!;
        var now = DateTime.UtcNow;
        // 着いたか：エリア移動（暗転）が終わっていて、エーテライトの 150m 以内。頼む前の場所は向こう側なので、ここには入らない。
        if (teleportIssuedAt != default && Me.Available && !BetweenAreas()
            && Vector2.Distance(new(Me.Position.X, Me.Position.Z), to.Position) <= ArrivedRange)
        {
            Count++;
            log.Write("Yanxia", $"{to.Name}に着きました");
            Restore("着きました");
            return;
        }
        if (now > phaseDeadline)
        {
            log.Write("Yanxia", $"{to.Name}へのテレポが {TeleportTimeout.TotalSeconds:F0} 秒で終わりませんでした");
            Restore("テレポが終わりませんでした");
            return;
        }
        if (teleportIssuedAt != default || now < nextTry)
            return;
        if (!lifestream.IsLoaded)
        {
            Restore("Lifestream がありません");
            return;
        }
        if (IsBusyForTeleport())
        {
            detail = "テレポできる状態になるのを待っています";
            return;
        }
        if (!lifestream.TryTeleport(to.Id, 0, out var accepted))
        {
            Restore("テレポを頼めませんでした");
            return;
        }
        if (!accepted)
        {
            // 乗り物に乗る途中などで、まだテレポを使えない。少しおいてやり直す（諦めるのは上の 60 秒の上限）。
            detail = $"{to.Name}へのテレポを受け付けてもらえません（やり直します）";
            nextTry = now + RetryInterval;
            return;
        }
        teleportIssuedAt = now;
        detail = $"{to.Name}へテレポしています";
    }

    /// <summary>GBR を元に戻す段へ。</summary>
    private void Restore(string reason)
    {
        detail = reason;
        phase = Phase.Restoring;
        phaseDeadline = DateTime.UtcNow + RestoreTimeout;
        nextTry = default;
        nextCheckAllowedAt = DateTime.UtcNow + CheckCooldown;
    }

    private void TickRestoring()
    {
        // エリア移動（暗転）が終わってから戻す。移動中に ON にしても動けない。
        if (BetweenAreas())
        {
            detail = "エリア移動が終わるのを待っています";
            return;
        }
        if (gbr.IsAutoGatherEnabled() == true)
        {
            log.Write("Yanxia", "自動採集を再開しました");
            Finish();
            return;
        }
        if (DateTime.UtcNow > phaseDeadline)
        {
            log.Write("Yanxia", "自動採集を戻せませんでした");
            Finish();
            return;
        }
        if (DateTime.UtcNow < nextTry) return;
        nextTry = DateTime.UtcNow + RetryInterval;
        issueGbrChange(() => gbr.SetAutoGatherEnabled(true));
        detail = "自動採集へ戻しています";
    }

    private void Finish()
    {
        phase = Phase.Idle;
        target = null;
        detail = "";
    }

    // ------------------------------------------------------------------
    // エーテライト（ゲームデータの地図の印から。アクセス済みかは 10 秒ごとに読み直す）

    private List<YanxiaRoute.Aetheryte>? aetherytes;
    private DateTime attunedAt;

    private List<YanxiaRoute.Aetheryte> Aetherytes()
    {
        aetherytes ??= LoadAetherytes(Svc.Data, YanxiaTerritory);
        if (DateTime.UtcNow - attunedAt > TimeSpan.FromSeconds(10))
        {
            attunedAt = DateTime.UtcNow;
            var attuned = Svc.Aetherytes.Select(a => a.AetheryteId).ToHashSet();
            aetherytes = aetherytes.Select(a => a with { Attuned = attuned.Contains(a.Id) }).ToList();
        }
        return aetherytes;
    }

    /// <summary>
    /// そのエリアのエーテライト（Aetheryte の IsAetheryte）の位置を、エリアの地図の印（MapMarker の DataType 3＝エーテライト）から求める。
    /// Aetheryte シートの Level は使えない（9 月に実測で無効と確かめた）。ヤンサでは 烈士庵 (245, -404)・ナマイ村 (433, -91)（2026-10-07 調べ）。
    /// アクセス済みかはここでは決めない（false。Aetherytes で読み直す）。
    /// </summary>
    public static List<YanxiaRoute.Aetheryte> LoadAetherytes(Dalamud.Plugin.Services.IDataManager data, uint territory)
    {
        var map = data.GetExcelSheet<TerritoryType>().GetRow(territory).Map.Value;
        var markers = data.GetSubrowExcelSheet<MapMarker>().GetRow(map.MapMarkerRange);
        var list = new List<YanxiaRoute.Aetheryte>();
        foreach (var a in data.GetExcelSheet<Aetheryte>().Where(a => a.Territory.RowId == territory && a.IsAetheryte))
        {
            var marker = markers.FirstOrDefault(m => m.DataType == 3 && m.DataKey.RowId == a.RowId);
            if (marker.DataType != 3) continue;
            list.Add(new(a.RowId, a.PlaceName.Value.Name.ExtractText(),
                YanxiaRoute.MarkerToWorld(marker.X, marker.Y, map.SizeFactor, map.OffsetX, map.OffsetY), false));
        }
        return list;
    }

    private static bool BetweenAreas()
        => Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51];

    /// <summary>テレポを頼めない状態か（9 月の版と同じ）。採集・釣りの途中にも手を出さない。</summary>
    private static bool IsBusyForTeleport()
        => Svc.Condition[ConditionFlag.InCombat] || Svc.Condition[ConditionFlag.Casting] || BetweenAreas()
           || Svc.Condition[ConditionFlag.Occupied] || Svc.Condition[ConditionFlag.Occupied33] || Svc.Condition[ConditionFlag.Occupied38]
           || Svc.Condition[ConditionFlag.Occupied39] || Svc.Condition[ConditionFlag.OccupiedInEvent] || Svc.Condition[ConditionFlag.OccupiedInQuestEvent]
           || Svc.Condition[ConditionFlag.Unconscious] || Svc.Condition[ConditionFlag.Gathering] || Svc.Condition[ConditionFlag.ExecutingGatheringAction]
           || Svc.Condition[ConditionFlag.Fishing];
}

/// <summary>
/// 詰まりの場所での見張り（9 月の版の StuckSpot。座標は利用者の実測）。経路の判断をすり抜けたときの備え。
/// 「動いていない」だけでは決めない（採集・釣り・人待ちでも止まる）：場所が近い・vnavmesh が動かそうとしている・それなのに進んでいない、の 3 つが揃ったとき。
/// </summary>
public sealed class YanxiaStuckSpots
{
    /// <param name="Wait">動けない状態がこれだけ続いたら詰まり。ゼロなら着いた時点で（動かそうとしていれば）すぐ。</param>
    public readonly record struct Spot(Vector3 Position, float Range, TimeSpan Wait, string Note);

    /// <summary>
    /// 詰まりの場所。範囲が重ならないようにする（重なると配列の順で先の方が使われる。9 月に 30m→15m に絞った）。待たない方を先に置く。
    /// </summary>
    // 座標の出どころ：上の 2 つは 2026-09-23 に利用者が実測（9 月の版と同じ。上は待たずに飛ぶ＝指定）、
    // 3 つ目は 2026-10-07 に利用者が報告した山の斜面（北 → 南の飛行の経路がここから 1〜16m で地形を突き抜ける。調べは YanxiaRoute）。
    // Note は記録に出る文なので、場所の説明だけにする（配布版を使う人の記録にも出る）。
    public static readonly Spot[] Spots =
    [
        new(new Vector3(307.5f, 10.5f, -373.0f), 15f, TimeSpan.Zero, "烈士庵の南の、来た時点で詰まる場所"),
        new(new Vector3(276.5f, 4.1f, -380.0f), 15f, TimeSpan.FromSeconds(5), "烈士庵の南の、壁の手前"),
        new(new Vector3(270.5f, 138.1f, -292.4f), 15f, TimeSpan.FromSeconds(5), "烈士庵の南の山の斜面"),
    ];

    /// <summary>これより動いていなければ、その場でうろうろしている（壁に当たりながら左右に振れる分があるので、完全な静止では測らない）。</summary>
    public const float StuckMoveThreshold = 8f;

    /// <summary>テレポしたあと、詰まりの見張りを止めておく時間（着地点が範囲に入っていても往復しない）。</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);

    private DateTime? since;
    private Vector3 anchor;
    private DateTime pausedUntil;

    public void Reset() => since = null;

    public void Pause(DateTime now)
    {
        pausedUntil = now + Cooldown;
        since = null;
    }

    /// <summary>詰まっていれば理由の文、そうでなければ null。moving＝vnavmesh が動かそうとしていて、採集の動作中でない。</summary>
    public string? Check(Vector3 me, bool moving, DateTime now)
    {
        if (now < pausedUntil) return null;
        Spot? here = null;
        foreach (var s in Spots)
            if (Vector3.Distance(me, s.Position) <= s.Range) { here = s; break; }
        if (here is not { } spot || !moving)
        {
            since = null;
            return null;
        }
        if (spot.Wait <= TimeSpan.Zero)
        {
            since = null;
            return $"{spot.Note} に入りました";
        }
        if (since is not { } start)
        {
            since = now;
            anchor = me;
            return null;
        }
        var moved = Vector3.Distance(me, anchor);
        if (moved > StuckMoveThreshold)
        {
            since = now;
            anchor = me;
            return null;
        }
        if (now - start < spot.Wait) return null;
        since = null;
        return $"{spot.Note} で {(now - start).TotalSeconds:F0} 秒のあいだ {moved:F1}m しか動いていません";
    }
}
