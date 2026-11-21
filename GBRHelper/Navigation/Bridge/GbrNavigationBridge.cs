using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;

namespace GBRHelper.Navigation;

public sealed class HookRegistrationException(MethodInfo method, int registered, Exception inner)
    : InvalidOperationException($"フック登録に失敗しました: {method.DeclaringType?.FullName}.{method.Name}", inner)
{
    public string MethodName { get; } = $"{method.DeclaringType?.FullName}.{method.Name}";
    public int RegisteredBeforeFailure { get; } = registered;
}

public interface IBridgeEnvironment
{
    bool MayProcess { get; }
    bool IsCurrent(RouteRequest request);
    bool IsFlying { get; }
    bool IsMounted { get; }
    bool IsDiving { get; }
    uint Territory { get; }
    Vector3 Position { get; }
    NavigationMode Mode { get; }
    RouteSettings Settings { get; }
    bool CanMonitorProgress { get; }
    double StallThresholdSeconds { get; }
    string NodeIdentity(Vector3 destination, uint? nodeId);
    List<Vector3>? RemainingWaypoints();
    float PathTolerance { get; }
    ProbeResult FinalConnection(Vector3 from, Vector3 to, bool fly);
    bool StopGather(RouteRequest request, string reason);
    void ReportMovement(MovementSnapshot snapshot, bool failure);
    void RecordStopResult(RouteRequest request, string result);
    void Record(string kind, RouteRequest? request, object? detail = null);
    void ObserveAttempt(RouteRequest request);
}

/// <summary>Owns only this helper's hooks. No shared delegate or navigation IPC is replaced.</summary>
public sealed class GbrNavigationBridge : IDisposable
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private readonly object owner;
    private readonly IBridgeEnvironment env;
    private readonly RouteProcessor processor;
    private readonly NavStateAccessor state;
    private readonly object recovery;
    private readonly List<IDisposable> hooks = new();
    private readonly Func<MethodInfo, Delegate, IDisposable> makeHook;
    private readonly Guid session = Guid.NewGuid();
    private long generation, requestId, attemptId;
    private int revision;
    private bool disposed, navigating;
    private uint? scopeItem;
    private Vector3? corrected;
    private RouteTicket? ticket;
    private RouteRequest? following;
    private RouteRequest? blockedRequest;
    private bool followingChanged;
    private RouteResult? followingResult;
    private OriginalLandingHandoff? originalLanding;
    private HashSet<Vector3> followingOriginal = new();
    private readonly ProgressWatch progress = new();
    // 飛行中の見直し：停滞の見張り・先の区間の当たり判定・避ける場所（同じ採取先の間だけ覚える）・求め直した回数。
    private readonly StallWatch stall = new();
    private readonly OrbitWatch orbit = new();
    private readonly FlightLookahead lookahead = new();
    private readonly List<AvoidZone> avoid = new();
    private int flightReplans;
    private bool replansExhaustedRecorded;
    private long nextLookahead;
    private string attemptKey = "";
    public string Status { get; private set; } = "接続準備中";
    public int HookCount => hooks.Count;
    public RouteRequest? CurrentRequest => ticket?.Request ?? following;
    private delegate void NodeOriginal<T>(object self, T target, object config);
    private delegate void NavigateOriginal(object self, Vector3 destination, bool fly, bool direct, uint? node);
    private delegate Vector3 CorrectOriginal(in Vector3 destination, in Vector3 player, uint? node);
    private delegate Vector3 CorrectHook(CorrectOriginal orig, in Vector3 destination, in Vector3 player, uint? node);
    private delegate bool CheckOriginal(object self, Vector3 destination, bool pathing);

    public GbrNavigationBridge(object autoGather, IBridgeEnvironment environment, RouteProcessor routeProcessor,
        Func<MethodInfo, Delegate, IDisposable>? hookFactory = null)
    {
        owner = autoGather; env = environment; processor = routeProcessor;
        var factory = hookFactory ?? ManagedHookRuntime.CreateHook;
        makeHook = (method, hook) =>
        {
            try { return factory(method, hook); }
            catch (Exception ex) { throw new HookRegistrationException(method, hooks.Count, ex); }
        };
        var t = owner.GetType(); state = new(t);
        recovery = t.GetField("_advancedUnstuck", Flags)?.GetValue(owner) ?? throw new MissingFieldException("_advancedUnstuck");
        try
        {
            var node = Method(t, "DoNodeMovement");
            GetType().GetMethod(nameof(InstallNode), Flags)!.MakeGenericMethod(node.GetParameters()[0].ParameterType).Invoke(this, [node]);
            hooks.Add(makeHook(Method(t, "Navigate"), (Action<NavigateOriginal, object, Vector3, bool, bool, uint?>)Navigate));
            hooks.Add(makeHook(Method(t, "GetCorrectedDestination"), (CorrectHook)Correct));
            hooks.Add(makeHook(Method(t, "HandlePathfinding"), (Action<Action<object>, object>)Handle));
            hooks.Add(makeHook(Method(t, "StopPathfinding"), (Action<Action<object>, object>)StopPathfinding));
            hooks.Add(makeHook(Method(t, "StopNavigation"), (Action<Action<object>, object>)StopNavigation));
            hooks.Add(makeHook(Method(recovery.GetType(), "Check"), (Func<CheckOriginal, object, Vector3, bool, bool>)Check));
            Status = "接続登録済み・実機での採取検証は未実施";
        }
        catch (Exception failure)
        {
            try { Dispose(); }
            catch (Exception cleanup) { throw new AggregateException("フック登録と解除の両方に失敗しました", failure, cleanup); }
            throw;
        }
    }
    private static MethodInfo Method(Type type, string name) => type.GetMethod(name, Flags) ?? throw new MissingMethodException(name);
    private static object? Property(object? value, string name) => value?.GetType().GetProperty(name, Flags)?.GetValue(value);
    private bool RecoveryRunning => Property(recovery, "IsRunning") is true;
    private bool IsPathing => Property(owner, "IsPathing") is true;
    private bool Allowed => !disposed && env.MayProcess && env.Mode != NavigationMode.Off && !env.IsDiving;
    private void InstallNode<T>(MethodInfo method)
        => hooks.Add(makeHook(method, (Action<NodeOriginal<T>, object, T, object>)Node));
    private void Node<T>(NodeOriginal<T> orig, object self, T next, object config)
    {
        var previous = scopeItem;
        try
        {
            scopeItem = null;
            if (ReferenceEquals(self, owner) && Allowed && Property(next, "Gatherable") is { } gatherable
                && Property(gatherable, "ItemId") is uint item) scopeItem = item;
        }
        catch (Exception ex) { Fault(ex); }
        try { orig(self, next, config); }
        finally { scopeItem = previous; }
    }
    private Vector3 Correct(CorrectOriginal orig, in Vector3 destination, in Vector3 player, uint? node)
    {
        var value = orig(in destination, in player, node);
        if (navigating && scopeItem.HasValue) corrected = value;
        return value;
    }
    private void Navigate(NavigateOriginal orig, object self, Vector3 destination, bool fly, bool direct, uint? node)
    {
        var own = ReferenceEquals(self, owner);
        var previousNavigation = navigating; var previousCorrection = corrected;
        var item = scopeItem;
        if (own) { navigating = true; corrected = null; }
        try
        {
            orig(self, destination, fly, direct, node); // exactly once, while the continuation is on the hook stack
            if (own && item.HasValue)
            {
                try
                {
                    var snapshot = state.Read(owner);
                    Capture(snapshot, item.Value, node, corrected ?? (snapshot.Offset ? ticket?.Request.CorrectedDestination : destination));
                }
                catch (Exception ex) { Fault(ex); }
            }
        }
        finally { navigating = previousNavigation; corrected = previousCorrection; }
    }
    private void Capture(NavSnapshot snapshot, uint item, uint? node, Vector3? correctedDestination)
    {
        if (!Allowed || RecoveryRunning || snapshot.Task == null || snapshot.Cancellation == null || correctedDestination == null) return;
        if (ReferenceEquals(snapshot.Task, ticket?.Proxy) || ReferenceEquals(snapshot.Task, ticket?.Original)) return;
        ResetPending();
        ClearFollowing();
        var identity = env.NodeIdentity(snapshot.Destination, node);
        var key = $"{env.Territory}/{item}/{identity}";
        if (key != attemptKey)
        {
            attemptKey = key; attemptId++; revision = 0;
            avoid.Clear(); flightReplans = 0; replansExhaustedRecorded = false;
        }
        var request = new RouteRequest(session, attemptId, ++requestId, ++generation, ++revision, env.Territory, item, identity,
            env.Position, snapshot.Destination, correctedDestination.Value, snapshot.Flying, snapshot.Combined, snapshot.Mounting, env.Settings,
            avoid.Count > 0 ? avoid.ToArray() : null);
        var next = new RouteTicket(request, snapshot.Task, snapshot.Cancellation, processor.ProcessAsync, env.Mode == NavigationMode.Observe);
        if (!state.Replace(owner, snapshot.Task, snapshot.Cancellation, next.Proxy)) { next.Invalidate(); return; }
        ticket = next;
        env.ObserveAttempt(request);
        env.Record("request", request, new { snapshot.Stage, snapshot.Direct, snapshot.Offset });
        Status = env.Mode == NavigationMode.Observe ? "観測のみ：経路は変更していません" : "元探索・経路検査を待っています";
    }
    private void Handle(Action<object> orig, object self)
    {
        if (!ReferenceEquals(self, owner)) { orig(self); return; }
        var execute = true;
        var held = ticket;
        try { execute = BeforeHandle(); }
        catch (Exception ex) { Fault(ex); ResetPending(); if (blockedRequest != null) execute = false; }
        if (!execute) return; // the whole existing Stop/MoveTo/state/Dismount transaction is skipped
        try { TrimReachedStart(held); } catch (Exception ex) { Fault(ex); }
        var prior = ticket;
        orig(self); // never retry after an original-method exception
        try
        {
            var after = state.Read(owner);
            if (prior != null && ReferenceEquals(prior, ticket) && after.Task != prior.Proxy && after.Task != prior.Original)
            {
                if (after.Task != null && after.Cancellation != null && Allowed && env.IsCurrent(prior.Request))
                {
                    // HandlePathfinding's direct fallback is generated here, outside Navigate.
                    ticket = null; prior.Invalidate();
                    Capture(after, prior.Request.ItemId, null, after.Destination);
                }
                else if (after.Task == null)
                {
                    following = prior.Request;
                    followingChanged = prior.Result?.Disposition is RouteDisposition.Refined or RouteDisposition.Detour;
                    followingResult = prior.Result;
                    followingOriginal = new(prior.OriginalPath);
                    originalLanding = prior.Result is { } processed ? OriginalLandingHandoff.Create(prior.Request, prior.OriginalPath, processed) : null;
                    Status = prior.Result?.Disposition switch
                    {
                        RouteDisposition.Refined => "GBRが曲線経路を追従中：最終接近と採取はGBRが担当",
                        RouteDisposition.Detour => "GBRが検査済み迂回候補を追従中：実際の取得結果は観測中",
                        _ => "従来経路で継続：" + (prior.Result?.Reason ?? "元探索の結果を使用"),
                    };
                    env.Record("consumed", prior.Request, prior.Result is { } r
                        ? new { r.Disposition, r.Reason, r.Searches, Path = r.Path.ToArray(), Original = prior.OriginalPath }
                        : null);
                    ticket = null;
                }
            }
        }
        catch (Exception ex) { Fault(ex); }
    }
    private bool BeforeHandle()
    {
        if (!Allowed) { ResetPending(); ClearFollowing(); return true; }
        if (blockedRequest is { } blocked && env.IsCurrent(blocked)) return false;
        blockedRequest = null;
        if (ticket is { } current)
        {
            if (!current.Valid || !env.IsCurrent(current.Request)) { ResetPending(); return true; }
            var s = state.Read(owner);
            if (!ReferenceEquals(s.Task, current.Proxy) || !ReferenceEquals(s.Cancellation, current.OriginalCancellation))
            { current.Invalidate(); ticket = null; return true; }
            if (!current.Proxy.IsCompletedSuccessfully) return true; // true original faults go to the original GBR handler
            if (current.Result is { Disposition: RouteDisposition.NoSafeCandidate } failure)
            {
                // 公式の経路に衝突が出て迂回が見つからなくても、GBR を止めない。返す点列は公式の経路そのもの（RouteTicket）で、
                // GBR だけで動かしたときと同じ動きになる。前の版はここで自動採集を止めていたが、飛行の側面 0.5m の線は
                // 地面や斜面に当たる誤判定が多く（SmoothNav のゲーム確認で公式の線は窓口で落とさない形に変えた理由）、
                // 公式より悪くなっていた（10-12 06:05 の停止）。
                Status = "従来経路で継続：" + failure.Reason;
                Report(current.Request, "Processing", "OriginalFallback", Status, result: failure);
                env.Record("original-fallback", current.Request, new { failure.Reason, failure.Code, failure.Probe });
                return true;
            }
            if (current.Result?.Disposition is RouteDisposition.Refined or RouteDisposition.Detour)
            {
                var path = GbrPathCodec.ForDispatch(current.Proxy.Result, s.Combined, env.Position, IsPathing);
                var check = path.Count == 0 ? ProbeResult.Unknown("通過点除去後に経路が空") : env.FinalConnection(env.Position, path[0], s.Flying && !s.Mounting);
                // Do not dispatch flight while the body is on the ground, or cross a landing boundary by trimming.
                var decoded = GbrPathCodec.Decode(current.Proxy.Result, s.Flying, s.Combined);
                var crossedLanding = s.Combined && decoded?.Landing is { } landing && !path.Contains(landing);
                if (check.Verdict != Verdict.Clear || crossedLanding)
                {
                    // 確かめられない・当たる接続線は使わず、公式の経路へ戻す（止めない。公式の経路は GBR だけのときと同じ動き）。
                    Status = "従来経路で継続：最終接続線または着地境界を確認できません";
                    env.Record("dispatch-fallback", current.Request, check);
                    Report(current.Request, "Dispatch", "OriginalFallback", Status, check, path, current.Result);
                    ResetPending(); // restore original BEFORE GBR mutates/consumes task, CTS, or stage
                }
            }
        }
        if (followingChanged && following is { } active)
        {
            if (!env.IsCurrent(active) || active.Settings != env.Settings) { ClearFollowing(); return true; }
            var s = state.Read(owner);
            var remaining = env.RemainingWaypoints();
            if (remaining == null) return StopBlocked(active, "改善経路の残点を読めません", "Following", "WaypointsUnavailable");
            var landingSwitch = s.Flying && s.Stage == 3 && !env.IsDiving && remaining.Count < s.LandCount;
            var mountSwitch = s.Flying && s.Mounting && env.IsMounted;
            if (landingSwitch || mountSwitch)
            {
                var path = mountSwitch ? remaining.SkipWhile(p => Geometry.Horizontal(env.Position, p) < 4).ToList() : remaining;
                var check = path.Count == 0 ? ProbeResult.Unknown("形態切替後の経路が空") : env.FinalConnection(env.Position, path[0], !landingSwitch);
                var phase = landingSwitch ? "Landing" : "Mounting";
                if (check.Verdict != Verdict.Clear)
                {
                    // No waiting while vnavmesh is already moving.
                    // 接続線が GBR の元の経路と同じなら、GBR だけのときと同じ切り替えなので任せる（判定の結果によらず）。
                    // 着地：元の着地の手前の線と地上の残り全部が一致（曲線は最後の区間と地上の終わりを変えない）。
                    // 騎乗：切り替え後の先頭の点が元の経路の点（曲線は出発から MountKeepDistance までを変えない）。
                    var original = landingSwitch
                        ? !s.Mounting && originalLanding?.Matches(remaining, env.Position, s.LandCount, env.PathTolerance) == true
                        : path.Count > 0 && followingOriginal.Contains(path[0]);
                    if (original)
                    {
                        Status = landingSwitch ? "着地検査を確認できず：元の着地経路と一致するためGBRへ復帰（取得は未確認）"
                            : "騎乗切替の接続線を確認できず：元の経路の点へつなぐためGBRへ任せる";
                        Report(active, phase, landingSwitch ? "OriginalLandingHandoff" : "OriginalMountHandoff", Status, check, path, followingResult);
                        env.Record(landingSwitch ? "landing-original-handoff" : "mount-original-handoff", active,
                            new { Probe = check, Remaining = remaining.ToArray(), Position = env.Position, s.LandCount });
                        if (landingSwitch) { followingChanged = false; originalLanding = null; }
                        return true; // GBR alone performs Stop/MoveTo/Dismount exactly once, on this same stack.
                    }
                    // 新しい線が確かに当たるときだけ止める。確かめられないだけなら GBR の切り替えに任せる（止めない）。
                    if (check.ReliableBlock)
                        return StopBlocked(active, "形態切替の接続線が衝突します: " + check.Reason,
                            phase, check.Code.ToString(), check, path, followingResult);
                    Status = "形態切替の接続線を確認できず：GBRの切替に任せて継続";
                    Report(active, phase, "UnverifiedHandoff", Status, check, path, followingResult);
                    env.Record("switch-unverified", active, new { Phase = phase, Probe = check, Path = path.ToArray(), Position = env.Position });
                    return true;
                }
                Report(active, phase, "Clear", "接続線を確認しGBRの形態切替を実行", check, path, followingResult);
            }
        }
        return true;
    }
    private bool StopBlocked(RouteRequest request, string reason, string phase, string code,
        ProbeResult? probe = null, List<Vector3>? path = null, RouteResult? result = null)
    {
        blockedRequest = request; // fail closed even if diagnostics or restoring the old task unexpectedly throws
        Report(request, phase, code, reason, probe, path, result ?? followingResult, true);
        try { ResetPending(); } catch (Exception ex) { Fault(ex); }
        ClearFollowing();
        blockedRequest = request;
        Status = "移動を中断（取得は未確認）：" + reason;
        try { env.Record("blocked", request, reason); } catch { }
        var stopped = false;
        try { stopped = env.StopGather(request, reason); } catch (Exception ex) { Fault(ex); }
        if (!stopped)
        {
            // Same setter as GatherBuddyReborn.SetAutoGatherEnabled(false); never call AbortAutoGather (it can refine/teleport).
            var enabled = owner.GetType().GetProperty("Enabled", Flags) ?? throw new MissingMemberException("Enabled");
            enabled.SetValue(owner, false);
            if (enabled.GetValue(owner) is not false) throw new InvalidOperationException("GBRの停止を確認できません");
            env.RecordStopResult(request, "Enabled=falseで停止を確認しました");
        }
        return false;
    }
    private void Report(RouteRequest request, string phase, string code, string reason, ProbeResult? probe = null,
        List<Vector3>? path = null, RouteResult? result = null, bool failure = false)
    {
        try
        {
        var s = state.Read(owner);
        probe ??= result?.Probe;
        env.ReportMovement(new(DateTimeOffset.UtcNow, phase, code, reason, request, env.Position,
            path?.Count > 0 ? path[0] : null, s.Flying, s.Mounting, s.Stage, path?.Count, s.LandCount,
            probe, result?.Disposition, result?.OriginalBlocked == true || request.Evidence.ReliableBlock, result?.Searches ?? 0,
            probe?.Metrics?.ElapsedMilliseconds, phase == "Processing" ? request.Settings.RerouteMilliseconds + request.Settings.RefineMilliseconds : probe != null ? 1 : null), failure);
        }
        catch (Exception ex) { try { env.Record("movement-diagnostic-error", request, ex.ToString()); } catch { } }
    }
    private void ClearFollowing()
    {
        following = null; followingChanged = false; followingResult = null; originalLanding = null; followingOriginal = new();
        stall.Reset(); orbit.Reset(); lookahead.Reset();
    }
    private void StopPathfinding(Action<object> orig, object self)
    {
        if (ReferenceEquals(self, owner)) { try { ResetPending(); } catch (Exception ex) { Fault(ex); } }
        orig(self); // GBR registers CTS disposal against the ORIGINAL task
    }
    private void StopNavigation(Action<object> orig, object self)
    {
        if (ReferenceEquals(self, owner)) { try { ResetPending(); ClearFollowing(); } catch (Exception ex) { Fault(ex); } }
        orig(self); // acquisition observation intentionally survives normal arrival/StopNavigation
    }
    private bool Check(CheckOriginal orig, object self, Vector3 destination, bool pathing)
    {
        var reset = false;
        try
        {
            var own = ReferenceEquals(self, recovery) && Allowed && !RecoveryRunning;
            reset = own && ticket is { HoldingRecovery: true } t && env.IsCurrent(t.Request);
            if (own && !reset && following is { } active && env.Mode == NavigationMode.Improve && env.IsCurrent(active))
            {
                var s = state.Read(owner);
                // 飛んでいる間は飛行の見直し、それ以外は地上の停滞の見張り（前の版のまま）。
                var flying = s.Flying && env.IsFlying;
                if (flying) reset = WatchFlight(active, s, pathing);
                var decision = flying ? ProgressDecision.None : progress.Observe(active, env.Position, Environment.TickCount64, env.StallThresholdSeconds,
                    env.CanMonitorProgress && pathing && s.Task == null && !s.Mounting && !s.Offset && !s.Flying && !active.Flying);
                if (decision != ProgressDecision.None)
                {
                    reset = true;
                    env.Record("progress-recovery", active, decision.ToString());
                    if (decision == ProgressDecision.Exhausted) StopBlocked(active, "同じ採取先で停滞が再発し、再探索予算を使い切りました", "Progress", "RecoveryExhausted");
                    else
                    {
                        // Invoke real current entries, never a saved hook continuation, for this owner's new request.
                        Method(owner.GetType(), "StopNavigation").Invoke(owner, null);
                        var previous = scopeItem; scopeItem = active.ItemId;
                        try { Method(owner.GetType(), "Navigate").Invoke(owner, [active.Destination, false, s.Direct, null]); }
                        finally { scopeItem = previous; }
                    }
                }
            }
        }
        catch (Exception ex) { Fault(ex); }
        return orig(self, reset ? default : destination, reset ? false : pathing);
    }
    /// <summary>
    /// 飛行中の見直し（GBR の詰まりの判定の入口から毎フレーム呼ばれる）。
    /// 前の版は出発の時に 1 回だけ経路を確かめ、空中の停滞は GBR 自身のランダムな押し出しに任せていた
    /// （10-12 嘆きの海：620m 先の壁は出発の時に「確認できない」で、押し出しの後も同じ経路で同じ壁へ戻り 2 分止まった）。
    /// B：GBR より先に停滞に気づいたら、止まった所を「避ける場所」にして経路を求め直させる。
    /// A：先の LookaheadDistance までの区間を当たり判定で確かめ、衝突が見つかったら、その所を避ける場所にして求め直させる。
    /// 求め直しは GBR の StopNavigation を呼ぶだけ：同じフレームで GBR が自分で Navigate を呼び、その要求に避ける場所が付く。
    /// 1 つの採取先で MaxFlightReplans 回まで。使い切ったら GBR 自身の詰まり対策に任せる（自動採集は止めない）。
    /// 戻り値 true＝今回は GBR の詰まりの判定を空にする（求め直したので GBR の押し出しを重ねない）。
    /// </summary>
    private bool WatchFlight(RouteRequest active, NavSnapshot s, bool pathing)
    {
        var c = env.Settings;
        if (!c.Flight || !c.Detours) return false;
        if (!(env.CanMonitorProgress && pathing && s.Task == null && !s.Mounting && !env.IsDiving)) { stall.Reset(); return false; }
        if (flightReplans >= c.MaxFlightReplans)
        {
            if (!replansExhaustedRecorded)
            {
                replansExhaustedRecorded = true;
                Status = "飛行中の求め直しの上限に達しました：GBR の詰まり対策に任せます";
                env.Record("flight-replan-exhausted", active, new { Count = flightReplans, Zones = avoid.ToArray(), Position = env.Position });
            }
            return false;
        }
        var remaining = env.RemainingWaypoints();
        if (remaining == null || remaining.Count == 0) return false;
        var position = env.Position;
        var now = Environment.TickCount64;
        if (stall.Observe(position, now, env.StallThresholdSeconds, true))
        {
            var heading = Heading(position, remaining, active.Destination);
            return Replan(active, "flight-stall", "飛行中に進めなくなりました",
                new AvoidZone(position, heading, c.AvoidRadius, "飛行中の停滞"), null, remaining);
        }
        // 次の点の近くで回り続けている（停滞の見張りは 2m 動けば進んだとみなすので気づけない）。
        if (orbit.Observe(position, remaining[0], now, env.StallThresholdSeconds, c.OrbitRadius, true))
            return Replan(active, "flight-orbit", "次の点の周りを回り続けています", null, null, remaining);
        // 着地の後の地上の点と、着地点（または終点）へ降りる最後の線は確かめない。飛行の身体の線は地面すれすれの線で
        // 地面に当たったと誤って出やすく、着地の切り替えの線は GBR の切り替えの所で別に確かめている。
        var flightCount = (s.Combined && s.Stage == 3 ? remaining.Count - s.LandCount + 1 : remaining.Count) - 1;
        if (flightCount <= 0 || now < nextLookahead) return false;
        nextLookahead = now + 100; // 確かめる間隔（フレームを重くしない。待ってから進むのではない）
        var look = lookahead.Step(position, remaining, flightCount, c.LookaheadDistance, (a, b) => env.FinalConnection(a, b, true),
            ignoreNear: c.LookaheadIgnoreNear);
        if (look.Verdict != LookaheadVerdict.Blocked || look.Segment is not { } segment || look.Probe is not { } probe) return false;
        var point = probe.Point ?? (segment.From + segment.To) * .5f;
        // 既に避ける場所として渡した所（それでも公式の経路しか無かった所）では、もう一度求め直さない。
        if (avoid.Any(z => Vector3.Distance(z.Point, point) <= z.Radius * 2)) return false;
        return Replan(active, "lookahead-blocked", "先の区間に衝突が見つかりました: " + probe.Reason,
            new AvoidZone(point, segment.To - segment.From, c.AvoidRadius, "先の区間の衝突"), probe, remaining);
    }
    /// <summary>
    /// 空中で渡す経路（この橋渡しが受け持った要求の結果か、戻した公式の結果）の先頭から、自分のすぐそばの点を外す。
    /// 空中で求め直すと、求めた瞬間の位置が先頭の点になる。渡すまでに少しでも動くと、vnavmesh はその点（0.25m 以内に
    /// 入らないと通ったことにしない）の周りを回り続ける（10-12 パーラカの里のエーテライト）。
    /// GBR は空中で受け取った点の並びをそのまま vnavmesh へ渡すので、渡す直前に同じ並びを直す。
    /// </summary>
    private void TrimReachedStart(RouteTicket? held)
    {
        if (held == null || !Allowed || !env.IsFlying) return;
        var s = state.Read(owner);
        if (!s.Flying || s.Mounting || s.Task is not { IsCompletedSuccessfully: true } task) return;
        if (!ReferenceEquals(task, held.Proxy) && !ReferenceEquals(task, held.Original)) return;
        var path = task.Result;
        var position = env.Position;
        var candidate = path.ToList();
        var removed = GbrPathCodec.TrimReached(candidate, position, s.Combined, env.Settings.ReachedRadius);
        if (removed == 0) return;
        // Trimming changes the first connector even when processing kept the original route.
        // Check the point GBR will actually use after removing its marker and passed waypoints.
        // Leave the original list intact if the new line is blocked, unverified, or empty.
        var dispatch = GbrPathCodec.ForDispatch(candidate, s.Combined, position, IsPathing);
        if (dispatch.Count == 0) return;
        var check = env.FinalConnection(position, dispatch[0], true);
        if (check.Verdict != Verdict.Clear)
        {
            env.Record("dispatch-trim-skipped", held.Request, new { Probe = check, Position = position, First = dispatch[0] });
            return;
        }
        var first = path[0];
        path.RemoveRange(0, removed);
        env.Record("dispatch-trim", held.Request, new { Removed = removed, Position = position, Dropped = first, First = path[0] });
    }
    private static Vector3 Heading(Vector3 position, List<Vector3> remaining, Vector3 destination)
    {
        foreach (var p in remaining) if (Vector3.Distance(p, position) > 1) return p - position;
        return destination - position;
    }
    private bool Replan(RouteRequest active, string kind, string reason, AvoidZone? zone, ProbeResult? probe, List<Vector3> remaining)
    {
        flightReplans++;
        if (zone != null) avoid.Add(zone);
        Status = "飛行中に経路を求め直します：" + reason;
        env.Record(kind, active, new { Reason = reason, Position = env.Position, Zone = zone, Probe = probe,
            Remaining = remaining.Take(32).ToArray(), Count = flightReplans, Limit = env.Settings.MaxFlightReplans });
        Report(active, "Flight", kind switch { "flight-stall" => "FlightStall", "flight-orbit" => "FlightOrbit", _ => "LookaheadBlocked" }, reason, probe);
        // GBR の本物の入口を呼ぶ（フックの続きは使わない）。このフレームのうちに GBR が Navigate を呼び直す。
        Method(owner.GetType(), "StopNavigation").Invoke(owner, null);
        return true;
    }
    public void ResetPending()
    {
        var old = ticket; ticket = null; generation++;
        progress.ResetTracking();
        if (old == null) return;
        old.Invalidate();
        state.Replace(owner, old.Proxy, old.OriginalCancellation, old.Original);
    }
    private void Fault(Exception ex)
    {
        Status = "接続処理の例外：従来経路へ復帰";
        try { env.Record("bridge-error", CurrentRequest, ex.GetType().Name + ": " + ex.Message); } catch { }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { ResetPending(); } finally
        {
            ClearFollowing();
            foreach (var hook in hooks.AsEnumerable().Reverse())
                try { hook.Dispose(); } catch (Exception ex) { Fault(ex); }
            hooks.Clear();
        }
    }
}
