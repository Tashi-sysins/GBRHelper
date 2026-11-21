using System.Numerics;
using System.Text.Json;
using GBRHelper.Navigation;

namespace NavigationTests;

public static partial class Suite
{
    public static string TestDataDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "TestData");
    private static (RouteRequest Request, List<Vector3> Original, RouteResult Result, Vector3 Position) RecordedLanding()
    {
        var entries = File.ReadAllLines(Path.Combine(TestDataDirectory, "landing-timeout.jsonl"))
            .Select(s => JsonSerializer.Deserialize<JsonElement>(s)).ToList();
        var options = new JsonSerializerOptions { IncludeFields = true };
        var consumed = entries.Single(e => e.GetProperty("Kind").GetString() == "consumed");
        var request = consumed.GetProperty("Request").Deserialize<RouteRequest>(options)!;
        var detail = consumed.GetProperty("Detail");
        var position = entries.Last(e => e.GetProperty("Kind").GetString() == "position").GetProperty("Detail").GetProperty("Position").Deserialize<Vector3>(options);
        return (request, detail.GetProperty("Original").Deserialize<List<Vector3>>(options)!,
            new(RouteDisposition.Refined, detail.GetProperty("Path").Deserialize<List<Vector3>>(options)!, "recorded"), position);
    }
    private static async Task<(FakeAuto Auto, FakeEnvironment Env, GbrNavigationBridge Bridge)> LandingBridge()
    {
        var data = RecordedLanding();
        var auto = new FakeAuto { Fly = true, Direct = false, Destination = data.Request.Destination, Next = Task.FromResult(data.Original) };
        var env = new FakeEnvironment { Mode = NavigationMode.Improve, Settings = data.Request.Settings,
            Territory = data.Request.Territory, Position = data.Request.Start, OnStop = auto.StopNavigation, IsFlying = true, IsMounted = true };
        var bridge = new GbrNavigationBridge(auto, env, new(new FakeQueries()));
        auto.DoNodeMovement(new(new(data.Request.ItemId)), new());
        await new NavStateAccessor(typeof(FakeAuto)).Read(auto).Task!;
        auto.HandlePathfinding(); Assert(auto.Moves == 1);
        var decoded = GbrPathCodec.Decode(data.Original, true, true)!;
        env.Remaining = decoded.Parts[1].Points.Skip(1).ToList(); auto.LiveRemaining = env.Remaining;
        env.Position = data.Position; auto.SeedLandingSwitch();
        return (auto, env, bridge);
    }
    private static async Task LandingRegressionTests()
    {
        await Test("L01 deterministic ground timeout resumes without repeating completed samples", SyncTask(() => {
            double time = 0; var nearestCalls = 0; var completed = new List<Vector3>();
            var probe = new GroundSegmentProbe((p, _, _) => { nearestCalls++; time += .3; return p; },
                (_, b) => { completed.Add(b); time += .5; return ProbeResult.Clear; }, () => time);
            var cursor = probe.Begin(Vector3.Zero, new(2,0,0)); var result = cursor.Advance(1);
            Assert(result.Code == ProbeCode.TimeBudget && !cursor.Complete && result.Metrics!.Samples == 1);
            var slices = 1;
            while (!cursor.Complete && slices++ < 20) result = cursor.Advance(1);
            Assert(result.Verdict == Verdict.Clear && completed.Count == 8 && completed.Distinct().Count() == 8 && nearestCalls == 9);
            Assert(result.Metrics!.NearestMilliseconds > 0 && result.Metrics.BodyMilliseconds > 0 && result.Metrics.Slices > 1);
        }));
        await Test("L02 sliced ground probe never loses a late wall or invents Clear", SyncTask(() => {
            var probe = new GroundSegmentProbe((p, _, _) => p, (_, b) => b.X > 1
                ? new(Verdict.Blocked, "wall", EvidenceKind.Collision, Code: ProbeCode.Collision) : ProbeResult.Clear, () => 0);
            var cursor = probe.Begin(Vector3.Zero, new(2,0,0));
            Assert(cursor.Advance(1, 2).Code == ProbeCode.TimeBudget);
            Assert(cursor.Advance(1, 2).Code == ProbeCode.TimeBudget);
            var result = cursor.Advance(1, 2); Assert(result.ReliableBlock && cursor.Complete);
            Assert(cursor.Advance().ReliableBlock);
        }));
        await Test("L03 exact recorded landing tail qualifies without claiming Clear", SyncTask(() => {
            var f = RecordedLanding(); var proof = OriginalLandingHandoff.Create(f.Request, f.Original, f.Result);
            var ground = GbrPathCodec.Decode(f.Original, true, true)!.Parts[1].Points;
            Assert(proof != null && proof.Matches(ground.Skip(1).ToList(), f.Position, ground.Count, .25f));
            Assert(!proof!.Matches(ground.Skip(1).ToList(), f.Position + new Vector3(2,0,0), ground.Count, .25f));
            Assert(!proof.Matches([], f.Position, ground.Count, .25f));
            Assert(!proof.Matches(ground.Skip(1).ToList(), f.Position, ground.Count + 1, .25f));
            Assert(!proof.Matches(ground.Skip(1).ToList(), f.Position, ground.Count, float.NaN));
            var changed = ground.Skip(1).ToList(); changed[0] += new Vector3(.01f, 0, 0);
            Assert(!proof.Matches(changed, f.Position, ground.Count, .25f));
            var altered = f.Result.Path.ToList(); altered[^3] += new Vector3(.01f, 0, 0);
            Assert(OriginalLandingHandoff.Create(f.Request, f.Original, f.Result with { Path = altered }) == null);
            var decoded = GbrPathCodec.Decode(f.Result.Path, true, true)!;
            decoded.Parts[0].Points[^2] += Vector3.UnitX;
            Assert(OriginalLandingHandoff.Create(f.Request, f.Original, f.Result with { Path = decoded.Encode() }) == null);
            Assert(OriginalLandingHandoff.Create(f.Request, f.Original, f.Result with { OriginalBlocked = true }) == null);
        }));
        await Test("L04 recorded landing TimeBudget returns to original GBR transition exactly once", async () => {
            var (auto, env, bridge) = await LandingBridge(); using (bridge)
            {
                env.Final = ProbeResult.Unknown("地上照会の時間上限", ProbeCode.TimeBudget);
                auto.HandlePathfinding(); auto.HandlePathfinding();
                Assert(env.StopCount == 0 && auto.LandingTransitions == 1 && auto.Dismounts == 1 && auto.Moves == 2);
                Assert(env.Movement.Current?.Code == "OriginalLandingHandoff" && env.Movement.Current.Probe?.Verdict == Verdict.Unknown);
                Assert(auto.Dispatched!.SequenceEqual(env.Remaining!) && env.Movement.LastFailure == null);
            }
        });
        await Test("L05 Clear landing completes original transaction once", async () => {
            var (auto, env, bridge) = await LandingBridge(); using (bridge)
            {
                auto.HandlePathfinding(); auto.HandlePathfinding();
                Assert(env.StopCount == 0 && auto.LandingTransitions == 1 && auto.Dismounts == 1);
                Assert(env.Movement.Current?.Code == "Clear");
            }
        });
        // 元の着地の線と地上の残りが一致するなら、判定の結果によらず GBR の切り替えに任せる（GBR だけのときと同じ線。止めない）。
        foreach (var code in new[] { ProbeCode.Unloaded, ProbeCode.QueryException, ProbeCode.AmbiguousSurface })
            await Test("L06 persistent " + code + " on the original landing tail hands back without stopping", async () => {
                var (auto, env, bridge) = await LandingBridge(); using (bridge)
                {
                    env.Final = ProbeResult.Unknown("unavailable", code); auto.HandlePathfinding(); auto.HandlePathfinding();
                    Assert(env.StopCount == 0 && auto.LandingTransitions == 1 && auto.Dismounts == 1);
                    Assert(env.Movement.Current?.Code == "OriginalLandingHandoff" && env.Movement.LastFailure == null);
                }
            });
        await Test("L07 reliable collision on the original landing tail hands back without stopping", async () => {
            var (auto, env, bridge) = await LandingBridge(); using (bridge)
            {
                env.Final = new(Verdict.Blocked, "wall", EvidenceKind.Collision, env.Position, Vector3.UnitX, ProbeCode.Collision);
                auto.HandlePathfinding(); Assert(env.StopCount == 0 && auto.LandingTransitions == 1 && auto.Moves == 2);
                Assert(env.Movement.Current?.Code == "OriginalLandingHandoff" && env.Movement.Current.Probe?.ReliableBlock == true);
            }
        });
        await Test("L08 timeout on changed remaining path is left to GBR without stopping", async () => {
            var (auto, env, bridge) = await LandingBridge(); using (bridge)
            {
                env.Remaining![0] += Vector3.UnitX; env.Final = ProbeResult.Unknown("budget", ProbeCode.TimeBudget);
                auto.HandlePathfinding(); Assert(env.StopCount == 0 && auto.LandingTransitions == 1);
                Assert(env.Movement.Current?.Code == "UnverifiedHandoff");
            }
        });
        await Test("L08 reliable collision on a changed connector stops once", async () => {
            var (auto, env, bridge) = await LandingBridge(); using (bridge)
            {
                env.Remaining![0] += Vector3.UnitX;
                env.Final = new(Verdict.Blocked, "wall", EvidenceKind.Collision, env.Position, Vector3.UnitX, ProbeCode.Collision);
                auto.HandlePathfinding(); auto.HandlePathfinding();
                Assert(env.StopCount == 1 && auto.LandingTransitions == 0 && auto.Dismounts == 0);
                var failure = env.Movement.LastFailure!;
                Assert(failure.Phase == "Landing" && failure.Code == "Collision" && failure.Request != null && failure.StopResult == "stopped");
                Assert(env.Events.Single(e => e.Kind == "stop").Request == failure.Request);
            }
        });
        await Test("L09 stale target cannot apply prior landing proof or stop another request", async () => {
            var (auto, env, bridge) = await LandingBridge(); using (bridge)
            {
                env.Current = false; env.Final = ProbeResult.Unknown("budget", ProbeCode.TimeBudget);
                auto.HandlePathfinding(); Assert(env.StopCount == 0 && env.Movement.Current == null);
            }
        });
        await Test("L10 failure survives reset and storage failure in copied JSON", async () => {
            var (auto, env, bridge) = await LandingBridge(); using (bridge)
            {
                env.Remaining![0] += Vector3.UnitX;
                env.Final = new(Verdict.Blocked, "wall", EvidenceKind.Collision, env.Position, Vector3.UnitX, ProbeCode.Collision); auto.HandlePathfinding();
                var failure = env.Movement.LastFailure;
                env.Movement.ResetCurrent();
                using var json = JsonDocument.Parse(env.Movement.ToText("trace.jsonl", 7, "disk full", null));
                var last = json.RootElement.GetProperty("LastFailure");
                Assert(last.GetProperty("Request").GetProperty("Id").GetInt64() == failure!.Request!.Id);
                Assert(last.GetProperty("Probe").GetProperty("Code").GetString() == "Collision");
                Assert(last.GetProperty("Position").TryGetProperty("X", out _));
                Assert(json.RootElement.GetProperty("StorageError").GetString() == "disk full");
                env.Movement.Clear(); Assert(env.Movement.LastFailure == null);
            }
        });
        await FrameworkSliceTests();
        await Test("L14 mounting switch to a point of the original route is left to GBR", async () => {
            var (auto, env, bridge) = await LandingBridge(); using (bridge)
            {
                var original = RecordedLanding().Original;
                auto.SeedMountSwitch(); env.Final = ProbeResult.Unknown("budget", ProbeCode.TimeBudget);
                env.Remaining = [original.First(p => Geometry.Horizontal(p, env.Position) >= 4)];
                auto.HandlePathfinding(); Assert(env.StopCount == 0 && auto.LandingTransitions == 0 && auto.Moves == 1);
                Assert(env.Movement.Current?.Phase == "Mounting" && env.Movement.Current.Code == "OriginalMountHandoff");
            }
        });
        await Test("L14 unverified mounting switch to a new point is left to GBR, a reliable collision stops", async () => {
            var (auto, env, bridge) = await LandingBridge(); using (bridge)
            {
                auto.SeedMountSwitch(); env.Final = ProbeResult.Unknown("budget", ProbeCode.TimeBudget);
                env.Remaining = [env.Position + new Vector3(5,0,0)];
                auto.HandlePathfinding(); Assert(env.StopCount == 0 && env.Movement.Current?.Code == "UnverifiedHandoff");
                env.Final = new(Verdict.Blocked, "wall", EvidenceKind.Collision, Code: ProbeCode.Collision);
                auto.HandlePathfinding(); Assert(env.StopCount == 1 && env.Movement.LastFailure?.Phase == "Mounting");
            }
        });
        await Test("L15 diagnostic and primary stop exceptions cannot dispatch an unsafe transition", async () => {
            var (auto, env, bridge) = await LandingBridge(); using (bridge)
            {
                env.Remaining![0] += Vector3.UnitX;
                env.ThrowDiagnostics = env.ThrowStop = true;
                env.Final = new(Verdict.Blocked, "wall", EvidenceKind.Collision, Code: ProbeCode.Collision);
                auto.HandlePathfinding();
                Assert(!auto.Enabled && env.StopCount == 1 && auto.Moves == 1 && auto.LandingTransitions == 0);
                Assert(env.Movement.LastFailure?.StopResult == "Enabled=falseで停止を確認しました");
            }
        });
        await Test("L16 OFF detaches boundary proof without late helper action", async () => {
            var (auto, env, bridge) = await LandingBridge(); using (bridge)
            {
                env.Mode = NavigationMode.Off; env.Final = ProbeResult.Unknown("budget", ProbeCode.TimeBudget);
                auto.HandlePathfinding(); Assert(env.StopCount == 0 && env.Movement.Current == null);
            }
        });
        await Test("L17 missing waypoints yields distinct persistent failure", async () => {
            var (auto, env, bridge) = await LandingBridge(); using (bridge)
            {
                env.Remaining = null; auto.HandlePathfinding();
                Assert(env.StopCount == 1 && env.Movement.LastFailure?.Code == "WaypointsUnavailable");
            }
        });
    }

    private static async Task PumpUntil(Task task, FrameworkRouteQueries queries, Action? afterPump = null)
    {
        for (var i = 0; !task.IsCompleted && i < 1000; i++) { queries.Pump(); afterPump?.Invoke(); await Task.Delay(1); }
        Assert(task.IsCompleted, "bounded test pump expired");
    }
    private static async Task FrameworkSliceTests()
    {
        await Test("L11 production scheduler finishes long ground work with each sample once", async () => {
            GBRHelper.Svc.PluginInterface.Ready = true; GBRHelper.Svc.PluginInterface.Nearest = p => p;
            NativeCollisionProbe.Stamp = 1; NativeCollisionProbe.BodyCalls = 0; NativeCollisionProbe.Body = (_,_) => ProbeResult.Clear;
            using var queries = new FrameworkRouteQueries();
            var task = queries.CheckAsync([new(Vector3.Zero, new(40,0,0), false)], default);
            await PumpUntil(task, queries);
            var result = (await task)[0];
            Assert(result.Verdict == Verdict.Clear && result.Metrics!.Samples == 160 && result.Metrics.Slices >= 5);
            Assert(NativeCollisionProbe.BodyCalls == 160 && queries.Pending == 0);
        });
        await Test("L12 changed collision coverage invalidates partial evidence", async () => {
            NativeCollisionProbe.Stamp = 1; NativeCollisionProbe.BodyCalls = 0;
            NativeCollisionProbe.Body = (_,_) => { NativeCollisionProbe.Stamp = 2; return ProbeResult.Clear; };
            using var queries = new FrameworkRouteQueries();
            var task = queries.CheckAsync([new(Vector3.Zero, new(40,0,0), false)], default);
            await PumpUntil(task, queries); Assert((await task)[0].Code == ProbeCode.Unloaded);
        });
        await Test("L13 cancelled slices drain without a late Clear result", async () => {
            NativeCollisionProbe.Stamp = 1; NativeCollisionProbe.Body = (_,_) => ProbeResult.Clear;
            using var queries = new FrameworkRouteQueries(); using var c = new CancellationTokenSource();
            var task = queries.CheckAsync([new(Vector3.Zero, new(40,0,0), false)], c.Token); c.Cancel();
            await PumpUntil(task, queries);
            try { await task; Assert(false); } catch (OperationCanceledException) { }
            queries.Pump(); Assert(queries.Pending == 0);
        });
        await Test("L18 collision in an early segment survives cancellation of the remaining batch", async () => {
            NativeCollisionProbe.Stamp = 1;
            NativeCollisionProbe.Body = (a,_) => a.X == 0
                ? new(Verdict.Blocked, "early wall", EvidenceKind.Collision, Code: ProbeCode.Collision) : ProbeResult.Clear;
            using var queries = new FrameworkRouteQueries(); using var c = new CancellationTokenSource();
            var evidence = new RouteEvidence();
            var task = queries.CheckAsync([new(Vector3.Zero, Vector3.UnitX, false), new(Vector3.UnitX, new(200,0,0), false)],
                c.Token, _ => { evidence.MarkBlocked(); c.Cancel(); });
            await PumpUntil(task, queries);
            Assert(evidence.ReliableBlock, "collision must be published before the batch finishes");
            try { await task; Assert(false); } catch (OperationCanceledException) { }
            NativeCollisionProbe.Body = (_,_) => ProbeResult.Clear;
        });
        await Test("L19 processor exception after a published collision cannot return Original", async () => {
            var request = Request();
            var result = await new RouteProcessor(new EarlyBlockQueries()).ProcessAsync(request, Corners, default);
            Assert(result.Disposition == RouteDisposition.NoSafeCandidate && result.OriginalBlocked && result.Probe!.ReliableBlock);
            Assert(result.Code == "ProcessorException");
        });
    }
    private sealed class EarlyBlockQueries : IRouteQueries
    {
        public Task<ProbeResult[]> CheckAsync(IReadOnlyList<Segment> segments, CancellationToken cancel, Action<ProbeResult>? onReliableBlock = null)
        {
            onReliableBlock?.Invoke(new(Verdict.Blocked, "wall", EvidenceKind.Collision, Code: ProbeCode.Collision));
            return Task.FromException<ProbeResult[]>(new InvalidOperationException("later segment failed"));
        }
        public Task<List<Vector3>> FindAsync(Vector3 a, Vector3 b, bool fly, CancellationToken cancel) => throw new Exception("must not search");
        public Task<Vector3?> ProjectAsync(Vector3 p, bool fly, CancellationToken cancel) => throw new Exception("must not project");
    }
}
