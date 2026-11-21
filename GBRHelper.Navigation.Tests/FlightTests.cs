using System.Numerics;
using GBRHelper.Navigation;

namespace NavigationTests;

// 飛行中の見直し（10-12 嘆きの海で壁へ飛び続けた件）の試験。
// A：飛んでいる間に先の区間を当たり判定で確かめ直す。B：空中の停滞を GBR のランダムな押し出しより先に引き取る。
// 迂回の受け入れ：衝突の近くは「通れる」を求め、遠くの「確認できない」線は許す。
// 床の高さ y=0 の歩ける面（点から上下 halfY 以内に床があれば床の点を返す）。
public sealed class FloorOnly(FakeQueries inner) : IRouteQueries, SmoothNav.Core.IMeshPointQuery
{
    public Task<ProbeResult[]> CheckAsync(IReadOnlyList<Segment> segments, CancellationToken cancel, Action<ProbeResult>? onReliableBlock = null) => inner.CheckAsync(segments, cancel, onReliableBlock);
    public Task<List<Vector3>> FindAsync(Vector3 from, Vector3 to, bool flying, CancellationToken cancel) => inner.FindAsync(from, to, flying, cancel);
    public Task<Vector3?> ProjectAsync(Vector3 point, bool flying, CancellationToken cancel) => inner.ProjectAsync(point, flying, cancel);
    public Vector3? NearestPoint(Vector3 point, float halfExtentXZ, float halfExtentY) => Math.Abs(point.Y) <= halfExtentY ? point with { Y = 0 } : null;
}

public static partial class Suite
{
    private static readonly Vector3 Up = Vector3.UnitY;
    private static RouteRequest FlightRequest(IReadOnlyList<AvoidZone>? avoid = null, RouteSettings? settings = null)
        => Request(true, settings: settings ?? new(Flight: true)) with { Avoid = avoid };
    // x=20 の面に立つ壁（z -10..10・y 40..60）。飛行の線がこの壁を横切れば衝突。
    private static ProbeResult FlightWall(Segment s)
    {
        var a = s.From; var b = s.To;
        if ((a.X - 20) * (b.X - 20) > 0 || Math.Abs(b.X - a.X) < 1e-6f) return ProbeResult.Clear;
        var t = (20 - a.X) / (b.X - a.X); var p = a + (b - a) * t;
        return Math.Abs(p.Z) <= 10 && p.Y >= 40 && p.Y <= 60
            ? new(Verdict.Blocked, "wall", EvidenceKind.Collision, p, -Vector3.UnitX, ProbeCode.Collision) : ProbeResult.Clear;
    }
    private static async Task<(FakeAuto Auto, FakeEnvironment Env, GbrNavigationBridge Bridge)> FlyingBridge(List<Vector3> raw, RouteSettings settings, FakeQueries? q = null)
    {
        var auto = new FakeAuto { Fly = true, Direct = true, Destination = raw[^1], Next = Task.FromResult(raw) };
        var env = new FakeEnvironment { Mode = NavigationMode.Improve, Settings = settings, IsFlying = true, IsMounted = true,
            Position = raw[0], Remaining = raw.Skip(1).ToList(), StallThresholdSeconds = .01, OnStop = auto.StopNavigation };
        var bridge = new GbrNavigationBridge(auto, env, new(q ?? new FakeQueries()));
        await Navigate(auto);
        return (auto, env, bridge);
    }
    private static async Task Navigate(FakeAuto auto)
    {
        auto.DoNodeMovement(new(new(42)), new());
        await new NavStateAccessor(typeof(FakeAuto)).Read(auto).Task!;
        auto.HandlePathfinding();
    }

    private static async Task FlightTests()
    {
        await Test("F01 avoid zone blocks only lines entering it in the stalled direction", SyncTask(() => {
            var zone = new AvoidZone(Vector3.Zero, Vector3.UnitX, 3, "stall");
            var into = AvoidZones.Hit(new(new(-5, 0, 0), new(5, 0, 0), true), [zone]);
            Assert(into is { ReliableBlock: true, Evidence: EvidenceKind.Stall }, "同じ向きで通る線は通れない");
            Assert(AvoidZones.Hit(new(new(5, 0, 0), new(-5, 0, 0), true), [zone]) == null, "逆向き（戻る線）は使ってよい");
            Assert(AvoidZones.Hit(new(Vector3.Zero, new(0, 0, 8), true), [zone]) == null, "横へ離れる線は使ってよい");
            Assert(AvoidZones.Hit(new(new(-5, 0, 4), new(5, 0, 4), true), [zone]) == null, "半径の外");
            Assert(AvoidZones.Hit(new(new(-5, 0, 0), new(5, 0, 0), true), [zone with { Direction = Vector3.Zero }]) == null);
        }));
        await Test("F02 stall zone forces a flight detour although thin body rays pass", async () => {
            // 当たり判定の細い線は隙間を通る（すべて Clear）のに、騎乗の身体は抜けられず止まった。止まった所を避けて先の点へつなぐ。
            var raw = new List<Vector3> { new(0, 50, 0), new(40, 50, 0), new(80, 50, 0) };
            var q = new FakeQueries();
            var zone = new AvoidZone(raw[0], raw[1] - raw[0], 3, "stall");
            var r = await new RouteProcessor(q).ProcessAsync(FlightRequest([zone]), raw, default);
            Assert(r.Disposition == RouteDisposition.Detour && r.OriginalBlocked, r.Reason);
            Assert(r.Path[0] == raw[0] && r.Path[^1] == raw[^1]);
            Assert(Geometry.Lines(r.Path, true).All(s => AvoidZones.Hit(s, [zone]) == null), "迂回が止まった所へ同じ向きで入った");
            Assert(q.Searches == 0, "近くの中継点で先の点へつなげるなら探索は使わない");
        });
        await Test("F03 without zones the same thin-ray route stays official", async () => {
            var raw = new List<Vector3> { new(0, 50, 0), new(40, 50, 0), new(80, 50, 0) };
            var r = await new RouteProcessor(new FakeQueries()).ProcessAsync(FlightRequest(), raw, default);
            Assert(r.Disposition != RouteDisposition.Detour && !r.OriginalBlocked);
        });
        await Test("F04 flight detour around a wall is adopted although far lines are not loaded yet", async () => {
            // 前の版は迂回の線がすべて「通れる」でないと使わなかった。遠くは当たり判定が読み込まれていないので、長い飛行では迂回が使えなかった。
            var raw = new List<Vector3> { new(0, 50, 0), new(30, 50, 0), new(200, 50, 0), new(400, 50, 0) };
            var hit = new Vector3(20, 50, 0);
            var q = new FakeQueries { Probe = s => FlightWall(s) is { Verdict: Verdict.Blocked } b ? b
                : AvoidZones.Distance(hit, s.From, s.To) <= 40 ? ProbeResult.Clear : ProbeResult.Unknown("unloaded", ProbeCode.Unloaded) };
            var r = await new RouteProcessor(q).ProcessAsync(FlightRequest(), raw, default);
            Assert(r.Disposition == RouteDisposition.Detour, r.Reason + " " + r.Code);
            Assert(r.Path[0] == raw[0] && r.Path[^1] == raw[^1] && r.Path.Contains(raw[^2]));
            Assert(Geometry.Lines(r.Path, true).All(s => FlightWall(s).Verdict == Verdict.Clear), "迂回が壁を通った");
        });
        await Test("F04 a long official line unknown as a whole is accepted when its part near the collision is clear", async () => {
            // 衝突の 10m 先から 370m 先まで 1 本の公式の線。遠い端が読み込まれていないので線全体は「確認できない」。
            var raw = new List<Vector3> { new(0, 50, 0), new(30, 50, 0), new(400, 50, 0) };
            var q = new FakeQueries { Probe = s => FlightWall(s) is { Verdict: Verdict.Blocked } b ? b
                : Vector3.Distance(s.From, s.To) > 100 ? ProbeResult.Unknown("unloaded", ProbeCode.Unloaded) : ProbeResult.Clear };
            var r = await new RouteProcessor(q).ProcessAsync(FlightRequest(), raw, default);
            Assert(r.Disposition == RouteDisposition.Detour, r.Reason + " " + r.Code);
            Assert(r.Path[0] == raw[0] && r.Path[^1] == raw[^1]);
            Assert(Geometry.Lines(r.Path, true).All(s => FlightWall(s).Verdict == Verdict.Clear), "迂回が壁を通った");
        });
        await Test("F04 near part of a line is clipped to the radius", SyncTask(() => {
            var part = RouteProcessor.NearPart(new(new(0, 0, 0), new(100, 0, 0), true), new(10, 0, 0), 20)!;
            Assert(part.From == Vector3.Zero && Math.Abs(part.To.X - 30) < .01f);
            Assert(RouteProcessor.NearPart(new(new(0, 0, 0), new(10, 0, 0), true), new(5, 0, 0), 20) == null, "線全体なら確かめ直さない");
            Assert(RouteProcessor.NearPart(new(new(0, 0, 50), new(100, 0, 50), true), new(10, 0, 0), 20) == null, "近くを通らない");
        }));
        await Test("F05 flight detour lines near the collision must be verified clear", async () => {
            var raw = new List<Vector3> { new(0, 50, 0), new(30, 50, 0), new(200, 50, 0) };
            var q = new FakeQueries { Probe = s => FlightWall(s) is { Verdict: Verdict.Blocked } b ? b
                : OnLines(raw, s) ? ProbeResult.Clear : ProbeResult.Unknown("unloaded", ProbeCode.Unloaded) };
            var r = await new RouteProcessor(q).ProcessAsync(FlightRequest(), raw, default);
            Assert(r.Disposition == RouteDisposition.NoSafeCandidate && r.Path.SequenceEqual(raw), r.Reason);
        });
        await Test("F06 detour acceptance rule: blocked anywhere or unknown near the collision is rejected", SyncTask(() => {
            var lines = new List<Segment> { new(Vector3.Zero, new(40, 0, 0), true), new(new(40, 0, 0), new(300, 0, 0), true) };
            var c = new RouteSettings();
            var unknown = ProbeResult.Unknown("unloaded");
            Assert(RouteProcessor.Acceptable(lines, [ProbeResult.Clear, unknown], new(5, 0, 0), c), "遠くだけ不明なら使う");
            Assert(!RouteProcessor.Acceptable(lines, [unknown, ProbeResult.Clear], new(5, 0, 0), c), "近くが不明なら使わない");
            Assert(!RouteProcessor.Acceptable(lines, [ProbeResult.Clear, new(Verdict.Blocked, "mesh", EvidenceKind.Mesh)], new(5, 0, 0), c), "遠くでも通れない線は使わない");
            Assert(!RouteProcessor.Acceptable(lines, [ProbeResult.Clear], new(5, 0, 0), c), "答えの数が合わない");
        }));
        await Test("F07 lookahead finds a collision ahead only within the distance and remembers clear lines", SyncTask(() => {
            var look = new FlightLookahead(); var probes = new List<(Vector3, Vector3)>();
            var remaining = new List<Vector3> { new(10, 50, 0), new(30, 50, 0), new(100, 50, 0), new(200, 50, 0) };
            ProbeResult Probe(Vector3 a, Vector3 b) { probes.Add((a, b)); return FlightWall(new(a, b, true)); }
            var first = look.Step(new(0, 50, 0), remaining, remaining.Count, 60, Probe);
            Assert(first.Verdict == LookaheadVerdict.Blocked && first.Segment!.To == remaining[1], "20m 先の壁を見つける");
            probes.Clear();
            var clearRoute = new List<Vector3> { new(10, 50, 30), new(30, 50, 30), new(100, 50, 30), new(200, 50, 30) };
            var r1 = look.Step(new(0, 50, 30), clearRoute, clearRoute.Count, 60, Probe);
            Assert(r1.Verdict == LookaheadVerdict.Pending, "見る距離の端の区間は途中までしか確かめていない");
            Assert(probes.All(p => p.Item2.X <= 60.01f), "見る距離より先は確かめない（読み込まれていない）");
            var count = probes.Count; probes.Clear();
            look.Step(new(1, 50, 30), clearRoute, clearRoute.Count, 60, Probe);
            Assert(probes.Count == 1 && probes[0].Item2.X <= 61.01f, "通れると分かった区間は確かめ直さない（端の区間だけ）: " + count + "/" + probes.Count);
        }));
        await Test("F07 lookahead does not remember unknown lines and limits probes per step", SyncTask(() => {
            var look = new FlightLookahead(); var calls = 0;
            var remaining = Enumerable.Range(1, 10).Select(i => new Vector3(i * 5, 50, 0)).ToList();
            ProbeResult Unknown(Vector3 a, Vector3 b) { calls++; return ProbeResult.Unknown("unloaded"); }
            Assert(look.Step(Vector3.Zero with { Y = 50 }, remaining, remaining.Count, 60, Unknown, maxProbes: 3).Verdict == LookaheadVerdict.Pending && calls == 3);
            calls = 0; look.Step(Vector3.Zero with { Y = 50 }, remaining, remaining.Count, 60, Unknown, maxProbes: 3);
            Assert(calls == 3, "確認できない線は覚えず、次の回にまた確かめる");
            calls = 0; look.Step(Vector3.Zero with { Y = 50 }, remaining, 2, 60, Unknown);
            Assert(calls == 2, "着地の後の点（flightCount より後）は確かめない");
        }));
        await Test("F08 flight stall watch uses 3D movement", SyncTask(() => {
            var w = new StallWatch();
            Assert(!w.Observe(Vector3.Zero, 0, 2, true));
            Assert(!w.Observe(new(0, 3, 0), 1600, 2, true), "真上へ 3m 上がったのは進んでいる");
            Assert(!w.Observe(new(0, 3, 0), 2000, 2, true));
            Assert(w.Observe(new(0, 3.5f, 0), 3200, 2, true), "1.5 秒（GBR のしきい値の 3/4）進まなければ停滞");
            Assert(!w.Observe(new(0, 3.5f, 0), 3300, 2, true), "知らせたら測り直す");
            Assert(!w.Observe(new(0, 3.5f, 0), 9000, 2, false) && !w.Observe(new(0, 3.5f, 0), 9100, 2, true), "見張れない間は数えない");
        }));
        await Test("F09 bridge replans a flight stall with an avoid zone instead of GBR's random push", async () => {
            var raw = new List<Vector3> { new(0, 50, 0), new(40, 50, 0), new(80, 50, 0) };
            var (auto, env, bridge) = await FlyingBridge(raw, new(Flight: true));
            using var _ = bridge;
            Assert(auto.Moves == 1);
            auto.Recovery.Check(raw[^1], true); Assert(auto.Recovery.LastDestination == raw[^1] && auto.Stops == 0);
            await Task.Delay(1100);
            auto.Recovery.Check(raw[^1], true);
            Assert(env.Events.Any(e => e.Kind == "flight-stall") && auto.Stops == 1, "停滞を引き取って求め直す");
            Assert(auto.Recovery.LastDestination == default && !auto.Recovery.LastPathing, "GBR の押し出しを重ねない");
            await Navigate(auto);
            var request = env.Events.Last(e => e.Kind == "request").Request!;
            Assert(request.Avoid is { Count: 1 } zones && zones[0].Point == raw[0] && Vector3.Dot(Vector3.Normalize(zones[0].Direction), Vector3.UnitX) > .99f);
            Assert(env.StopCount == 0 && auto.Enabled, "自動採集は止めない");
        });
        await Test("F10 flight replans are bounded and then GBR's own recovery runs", async () => {
            var raw = new List<Vector3> { new(0, 50, 0), new(40, 50, 0), new(80, 50, 0) };
            var (auto, env, bridge) = await FlyingBridge(raw, new(Flight: true, MaxFlightReplans: 1));
            using var _ = bridge;
            auto.Recovery.Check(raw[^1], true); await Task.Delay(1100); auto.Recovery.Check(raw[^1], true);
            Assert(auto.Stops == 1);
            await Navigate(auto);
            auto.Recovery.Check(raw[^1], true); await Task.Delay(1100); auto.Recovery.Check(raw[^1], true);
            Assert(auto.Stops == 1 && env.Events.Count(e => e.Kind == "flight-replan-exhausted") == 1, "上限の後は求め直さない");
            Assert(auto.Recovery.LastDestination == raw[^1] && auto.Recovery.LastPathing, "上限の後は GBR 自身の詰まり対策に渡す");
            Assert(env.StopCount == 0 && auto.Enabled);
        });
        await Test("F11 bridge lookahead replans once for a collision found ahead", async () => {
            var raw = new List<Vector3> { new(0, 50, 0), new(40, 50, 0), new(80, 50, 0) };
            // 当たり判定は x=20 の壁だけ（出発の時の確かめでは読み込まれていない想定で、公式の経路のまま飛び始めている）。
            var (auto, env, bridge) = await FlyingBridge(raw, new(Flight: true), new FakeQueries { Probe = FlightWall });
            using var _ = bridge;
            env.FinalProbe = (a, b, _) => FlightWall(new(a, b, true));
            auto.Recovery.Check(raw[^1], true);
            Assert(env.Events.Any(e => e.Kind == "lookahead-blocked") && auto.Stops == 1);
            await Navigate(auto);
            var request = env.Events.Last(e => e.Kind == "request").Request!;
            Assert(request.Avoid is { Count: 1 } zones && zones[0].Point == new Vector3(20, 50, 0));
            Assert(env.Events.Count(e => e.Kind == "consumed") == 2, "求め直した経路を GBR が追い始めた（見張りが続く）");
            var consumed = env.Events.Last(e => e.Kind == "consumed").Detail!;
            Assert(consumed.GetType().GetProperty("Disposition")!.GetValue(consumed) is RouteDisposition.Detour, "壁を避けた迂回を渡した");
            await Task.Delay(150);
            auto.Recovery.Check(raw[^1], true);
            Assert(auto.Stops == 1, "避ける場所として渡した衝突では、もう一度求め直さない");
        });
        await Test("F13 lookahead does not probe the final descent line to the landing point", async () => {
            // 地面すれすれへ降りる最後の線は、飛行の身体の線が地面に当たったと誤って出やすい。着地の切り替えは GBR の所で確かめる。
            var raw = new List<Vector3> { new(0, 50, 0), new(40, 50, 0), new(45, 1, 0) };
            var (auto, env, bridge) = await FlyingBridge(raw, new(Flight: true));
            using var _ = bridge;
            var probed = new List<Vector3>();
            env.FinalProbe = (a, b, fly) => { probed.Add(b); return b == raw[^1]
                ? new(Verdict.Blocked, "ground", EvidenceKind.Collision, b, Vector3.UnitY, ProbeCode.Collision) : ProbeResult.Clear; };
            auto.Recovery.Check(raw[^1], true);
            Assert(auto.Stops == 0 && probed.Count == 1 && probed[0] == raw[1], "最後の線を確かめた: " + probed.Count);
        });
        await Test("F14 in-air dispatch drops leading points right next to the character", SyncTask(() => {
            // 10-12 パーラカの里：空中で求め直した経路の先頭（求めた瞬間の位置）から 0.5m ずれ、その点の周りを回り続けた。
            var direct = new List<Vector3> { new(0, 50, 0), new(.4f, 50, 0), new(30, 50, 0), new(60, 50, 0) };
            Assert(GbrPathCodec.TrimReached(direct, new(.5f, 50, .3f), false, 3) == 2 && direct[0] == new Vector3(30, 50, 0));
            var shortPath = new List<Vector3> { new(0, 50, 0), new(1, 50, 0) };
            Assert(GbrPathCodec.TrimReached(shortPath, Vector3.Zero with { Y = 50 }, false, 3) == 0 && shortPath.Count == 2, "飛行の最後の 2 点は残す");
            var land = new Vector3(2, 0, 0);
            var combined = new List<Vector3> { new(0, 50, 0), new(1, 50, 0), land, new(4, 0, 0), land };
            Assert(GbrPathCodec.TrimReached(combined, new(0, 50, 0), true, 3) == 1, "着地の目印の手前の 2 点は残す");
            Assert(combined.SequenceEqual(new[] { new Vector3(1, 50, 0), land, new(4, 0, 0), land }) && GbrPathCodec.Decode(combined, true, true) != null);
            var far = new List<Vector3> { new(0, 50, 0), new(30, 50, 0), new(60, 50, 0) };
            Assert(GbrPathCodec.TrimReached(far, new(5, 50, 0), false, 3) == 0, "3m より遠い点は外さない");
        }));
        await Test("F14 bridge trims the in-air dispatch GBR is about to hand to vnavmesh", async () => {
            var raw = new List<Vector3> { new(0, 50, 0), new(.4f, 50, 0), new(30, 50, 0), new(60, 50, 0) };
            var (auto, env, bridge) = await FlyingBridge(raw, new(Flight: true));
            using var _ = bridge;
            Assert(auto.Dispatched![0] == new Vector3(30, 50, 0) && auto.Dispatched[^1] == raw[^1], string.Join(" ", auto.Dispatched));
            Assert(env.Events.Any(e => e.Kind == "dispatch-trim"));
        });
        foreach (var verdict in new[] { Verdict.Blocked, Verdict.Unknown })
        {
            await Test($"F14 near-start trimming preserves the original path when the shortcut is {verdict}", async () => {
                var raw = new List<Vector3> { new(0, 50, 0), new(0, 50, 2), new(5, 50, 2), new(10, 50, 2) };
                var auto = new FakeAuto { Fly = true, Direct = true, Destination = raw[^1], Next = Task.FromResult(raw) };
                var checkedEnds = new List<Vector3>();
                var env = new FakeEnvironment { Mode = NavigationMode.Improve, Settings = new(Flight: true), IsFlying = true,
                    IsMounted = true, Position = raw[0], FinalProbe = (a, b, fly) => {
                        Assert(a == raw[0] && fly, "validate the actual flying connector");
                        checkedEnds.Add(b);
                        return new(verdict, "shortcut is not clear", EvidenceKind.Collision);
                    } };
                var q = new FakeQueries { Probe = _ => ProbeResult.Unknown("retain original route") };
                using var bridge = new GbrNavigationBridge(auto, env, new(q));
                await Navigate(auto);
                Assert(auto.Dispatched!.SequenceEqual(raw), "unsafe or unverified trimming must preserve every original waypoint");
                Assert(checkedEnds.SequenceEqual(new[] { raw[2] }), "check the new first waypoint before removing nearby points");
                Assert(auto.HandleCalls == 1 && auto.Moves == 1 && auto.Stops == 0 && env.StopCount == 0,
                    "the original dispatch still runs exactly once without stopping gathering");
                Assert(!env.Events.Any(e => e.Kind == "dispatch-trim"), "no trim was committed");
            });
        }
        await Test("F14 near-start trimming validates the connector after GBR's passed-waypoint pruning", async () => {
            var raw = new List<Vector3> { new(0, 50, 0), new(0, 50, 2), new(-5, 50, 2), new(10, 50, 2) };
            var auto = new FakeAuto { Fly = true, Direct = true, IsPathing = true, Destination = raw[^1], Next = Task.FromResult(raw) };
            var checkedEnds = new List<Vector3>();
            var env = new FakeEnvironment { Mode = NavigationMode.Improve, Settings = new(Flight: true), IsFlying = true,
                IsMounted = true, Position = raw[0], FinalProbe = (a, b, fly) => {
                    Assert(a == raw[0] && fly, "validate the actual flying connector");
                    checkedEnds.Add(b); return ProbeResult.Clear;
                } };
            var q = new FakeQueries { Probe = _ => ProbeResult.Unknown("retain original route") };
            using var bridge = new GbrNavigationBridge(auto, env, new(q));
            await Navigate(auto);
            Assert(checkedEnds.SequenceEqual(new[] { raw[^1] }), "the checked point must be the point GBR will actually dispatch");
            Assert(auto.Dispatched!.SequenceEqual(new[] { raw[^1] }) && auto.HandleCalls == 1 && auto.Moves == 1,
                "clear trimming and the original dispatch still run");
        });
        await Test("F14 checked near-start trimming preserves combined landing metadata and the ground tail", async () => {
            var landing = new Vector3(30, 0, 0);
            var raw = new List<Vector3> { new(0, 50, 0), new(1, 50, 0), new(20, 50, 0), landing, new(40, 0, 0), landing };
            var auto = new FakeAuto { Fly = true, Direct = false, Destination = raw[^2], Next = Task.FromResult(raw) };
            var checkedEnds = new List<Vector3>();
            var env = new FakeEnvironment { Mode = NavigationMode.Improve, Settings = new(Flight: true), IsFlying = true,
                IsMounted = true, Position = raw[0], FinalProbe = (a, b, fly) => { checkedEnds.Add(b); return ProbeResult.Clear; } };
            var q = new FakeQueries { Probe = _ => ProbeResult.Unknown("retain original route") };
            using var bridge = new GbrNavigationBridge(auto, env, new(q));
            await Navigate(auto);
            Assert(checkedEnds.SequenceEqual(new[] { raw[2] }), "validate the new flight start");
            Assert(auto.Dispatched!.SequenceEqual(new[] { raw[2], landing, raw[^2] }), "keep the landing point and full ground tail");
            Assert(new NavStateAccessor(typeof(FakeAuto)).Read(auto).LandCount == 2 && auto.HandleCalls == 1 && auto.Moves == 1,
                "GBR consumes the landing marker exactly once");
        });
        await Test("F14 dispatch on the ground or while mounting is not trimmed", async () => {
            var raw = new List<Vector3> { new(0, 50, 0), new(.4f, 50, 0), new(30, 50, 0), new(60, 50, 0) };
            var auto = new FakeAuto { Fly = true, Direct = true, Destination = raw[^1], Next = Task.FromResult(raw) };
            var env = new FakeEnvironment { Mode = NavigationMode.Improve, Settings = new(Flight: true), IsFlying = false, Position = raw[0] };
            using (new GbrNavigationBridge(auto, env, new(new FakeQueries()))) await Navigate(auto);
            Assert(auto.Dispatched![0] == raw[0], "地上（乗る前）は GBR の切り替えに任せる");
            var mounting = new FakeAuto { Fly = true, Direct = true, Mounting = true, Destination = raw[^1], Next = Task.FromResult(raw.ToList()) };
            var env2 = new FakeEnvironment { Mode = NavigationMode.Improve, Settings = new(Flight: true), IsFlying = true, Position = raw[0] };
            using (new GbrNavigationBridge(mounting, env2, new(new FakeQueries()))) await Navigate(mounting);
            Assert(mounting.Dispatched![0] == raw[0], "騎乗の切り替え中は GBR が近くの点を飛ばす");
        });
        await Test("F15 orbit watch notices circling around a near waypoint", SyncTask(() => {
            var w = new OrbitWatch(); var next = new Vector3(1, 50, 0);
            Assert(!w.Observe(new(0, 50, 3), next, 0, 2, 10, true));
            Assert(!w.Observe(new(0, 50, -3), next, 1000, 2, 10, true));
            Assert(w.Observe(new(0, 50, 3), next, 1600, 2, 10, true), "同じ点の近くで 1.5 秒通れない");
            Assert(!w.Observe(new(0, 50, 3), new(30, 50, 0), 1700, 2, 10, true) && !w.Observe(new(0, 50, 3), new(30, 50, 0), 4000, 2, 10, true), "点を通れば測り直す・遠い点は見ない");
            Assert(!w.Observe(new(0, 50, 3), next, 5000, 2, 10, false), "見張れない間は数えない");
        }));
        await Test("F15 bridge replans when circling a near waypoint although it keeps moving", async () => {
            var raw = new List<Vector3> { new(0, 50, 0), new(40, 50, 0), new(80, 50, 0) };
            var (auto, env, bridge) = await FlyingBridge(raw, new(Flight: true));
            using var _ = bridge;
            env.Remaining = [new(1, 50, 0), new(80, 50, 0)];
            env.Position = new(0, 50, 3); auto.Recovery.Check(raw[^1], true);
            await Task.Delay(1100);
            env.Position = new(0, 50, -3); auto.Recovery.Check(raw[^1], true);
            Assert(env.Events.Any(e => e.Kind == "flight-orbit") && !env.Events.Any(e => e.Kind == "flight-stall") && auto.Stops == 1);
            await Navigate(auto);
            Assert(env.Events.Last(e => e.Kind == "request").Request!.Avoid == null, "回り続けた点は避ける場所にしない");
        });
        await Test("F16 lookahead ignores a hit touching the body and still sees a farther wall", SyncTask(() => {
            // 10-12 オックハヌ：飛び立った瞬間、体から 0.5m の柱を衝突と判定して求め直し、飛び立ちを邪魔した。
            var look = new FlightLookahead(); var position = new Vector3(0, 50, 0);
            var remaining = new List<Vector3> { new(10, 50, 0), new(30, 50, 0) };
            ProbeResult Probe(Vector3 a, Vector3 b) => a == position
                ? new(Verdict.Blocked, "pillar", EvidenceKind.Collision, new(.5f, 50, 0), Code: ProbeCode.Collision)
                : FlightWall(new(a, b, true));
            var r = look.Step(position, remaining, remaining.Count, 60, Probe, ignoreNear: 3);
            Assert(r.Verdict == LookaheadVerdict.Blocked && r.Segment!.To == remaining[1], "触れている所は飛ばし、20m 先の壁は見つける");
            var only = look.Step(position, [new(10, 50, 0)], 1, 60, Probe, ignoreNear: 3);
            Assert(only.Verdict == LookaheadVerdict.Pending, "触れている所だけなら求め直さない（覚えもしない）");
        }));
        await Test("F16 bridge does not replan at takeoff for a pillar touching the body", async () => {
            var raw = new List<Vector3> { new(0, 6, 0), new(0, 8, 2), new(-80, 12, 60), new(-300, 12, 280) };
            var (auto, env, bridge) = await FlyingBridge(raw, new(Flight: true));
            using var _ = bridge;
            var probed = 0;
            env.FinalProbe = (a, b, fly) => { probed++; return a == raw[0]
                ? new(Verdict.Blocked, "pillar", EvidenceKind.Collision, a + new Vector3(.3f, 1, .4f), Code: ProbeCode.Collision) : ProbeResult.Clear; };
            auto.Recovery.Check(raw[^1], true);
            Assert(probed >= 2 && auto.Stops == 0 && !env.Events.Any(e => e.Kind == "lookahead-blocked"), "probed=" + probed + " stops=" + auto.Stops);
        });
        await Test("F17 flight detour middle points are never placed right above the walkable floor", async () => {
            // 10-12 オックハヌ：中継点が床の高さになり着地、次の点へ飛び直すのにジャンプを繰り返した。
            static ProbeResult LowWall(Segment s)
            {
                var a = s.From; var b = s.To;
                if ((a.X - 20) * (b.X - 20) > 0 || Math.Abs(b.X - a.X) < 1e-6f) return ProbeResult.Clear;
                var p = a + (b - a) * ((20 - a.X) / (b.X - a.X));
                // 高さ 12m まで（上へ 8m の中継点は越えられず、床すれすれの横の中継点が先に試される形）。
                return Math.Abs(p.Z) <= 10 && p.Y <= 12 ? new(Verdict.Blocked, "low wall", EvidenceKind.Collision, p, -Vector3.UnitX, ProbeCode.Collision) : ProbeResult.Clear;
            }
            var raw = new List<Vector3> { new(0, 2, 0), new(40, 2, 0), new(80, 2, 0) };
            var q = new FakeQueries { Probe = LowWall, Mesh = (p, half) => p with { Y = 0 } };
            var floorQueries = new FloorOnly(q);
            var r = await new RouteProcessor(floorQueries).ProcessAsync(FlightRequest(), raw, default);
            Assert(r.Disposition == RouteDisposition.Detour, r.Reason);
            Assert(r.Path.Skip(1).Take(r.Path.Count - 2).Where(p => !raw.Contains(p)).All(p => p.Y >= 2.5f), "床すれすれの中継点: " + string.Join(" ", r.Path));
            Assert(Geometry.Lines(r.Path, true).All(s => LowWall(s).Verdict == Verdict.Clear));
        });
        await Test("F12 flight watch stays off on the ground and while mounting", async () => {
            var raw = new List<Vector3> { new(0, 50, 0), new(40, 50, 0), new(80, 50, 0) };
            var (auto, env, bridge) = await FlyingBridge(raw, new(Flight: true));
            using var _ = bridge;
            env.IsFlying = false;
            env.Final = new(Verdict.Blocked, "wall", EvidenceKind.Collision, new(20, 50, 0), -Vector3.UnitX, ProbeCode.Collision);
            auto.Recovery.Check(raw[^1], true); await Task.Delay(1100); auto.Recovery.Check(raw[^1], true);
            Assert(auto.Stops == 0 && !env.Events.Any(e => e.Kind is "lookahead-blocked" or "flight-stall"));
            env.IsFlying = true; env.CanMonitorProgress = false;
            auto.Recovery.Check(raw[^1], true);
            Assert(auto.Stops == 0, "見張れない状態（詠唱・ジャンプなど）では求め直さない");
        });
    }
}
