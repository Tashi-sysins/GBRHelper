using System.Numerics;
using GBRHelper.Navigation;

namespace NavigationTests;

public static partial class Suite
{
    private static int passed, failed;
    private static readonly Vector3 A = new(1, 0, 0), B = new(8, 0, 0), C = new(8, 0, 8), D = new(16, 0, 8), E = new(18, 0, 8);
    private static List<Vector3> Corners => [A, B, C, D, E];
    private static RouteRequest Request(bool flying = false, bool combined = false, RouteSettings? settings = null)
        => new(Guid.NewGuid(), 1, 1, 1, 1, 123, 42, "node", A, E, E, flying, combined, false, settings ?? new());
    private static void Assert(bool condition, string message = "assertion") { if (!condition) throw new Exception(message); }
    private static async Task Test(string name, Func<Task> action)
    {
        try { await action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + "\n" + ex); }
    }
    private static Task Sync(Action action) { action(); return Task.CompletedTask; }
    public static async Task<int> Run()
    {
        await Test("U08 one-point path survives unchanged", async () => {
            var q = new FakeQueries(); var r = await new RouteProcessor(q).ProcessAsync(Request(), [A], default);
            Assert(r.Path.SequenceEqual(new[] { A }) && q.Checks == 0 && q.Searches == 0);
        });
        await Test("U07 empty and non-finite paths pass through without invented geometry", async () => {
            var q = new FakeQueries(); var p = new RouteProcessor(q);
            Assert((await p.ProcessAsync(Request(), [], default)).Path.Count == 0);
            var r = await p.ProcessAsync(Request(), [A, new(float.NaN, 0, 0), C], default);
            Assert(r.Disposition == RouteDisposition.Original && q.Checks == 0);
        });
        await Test("U10 ground curve preserves corrected endpoint and final approach", async () => {
            var q = new FakeQueries { Mesh = FakeQueries.Building }; var r = await new RouteProcessor(q).ProcessAsync(Request(), Corners, default);
            Assert(r.Disposition == RouteDisposition.Refined && !r.Path.SequenceEqual(Corners));
            Assert(r.Path[0] == A && r.Path[^2] == D && r.Path[^1] == E);
            Assert(Geometry.Length(r.Path) <= Geometry.Length(Corners) * 1.05f && q.Searches == 0);
            Assert(r.Path.All(p => !InsideBuilding(p)), "曲線が建物の中へ入った");
        });
        await Test("Shape ground curve swings outside a building corner instead of cutting into it", async () => {
            // 前の版の二次ベジェは角の内側（建物の中）を通る形で、歩ける面の照会で必ず落ちて曲線にならなかった（実機で地上 0/25）。
            var start = new Vector3(-20, 0, 0); var corner = new Vector3(8, 0, 0); var up = new Vector3(8, 0, 30); var end = new Vector3(8, 0, 34);
            var q = new FakeQueries { Mesh = FakeQueries.Building };
            var result = await new RouteProcessor(q).ProcessAsync(Request(), [start, corner, up, end], default);
            Assert(result.Disposition == RouteDisposition.Refined && !result.Path.Contains(corner), "建物の角が曲線にならない");
            Assert(result.Path.All(p => !InsideBuilding(p)), "曲線が建物の中へ入った");
            // 角の手前で外（z<0）へ、角の後も外（x>8）へ膨らんで回る（人が建物の角を大回りで曲がる形）。
            Assert(result.Path.Any(p => p.Z < -.3f) && result.Path.Any(p => p.X > corner.X + .3f), "角の外へ膨らんでいない");
            Assert(LargestTurn(result.Path) < 30, "90 度の角が残っている");
            Assert(q.MeshQueries > 0 && q.Checks == 3, "地上の新しい線は歩ける面で確かめる（当たり判定の照会は元の区間だけ）");
        });
        await Test("Shape short zigzag edges near a node are rounded (second stage)", async () => {
            // 採取点へ近づく所の、短い辺（0.6〜1m）が続く角。CurveBuilder の大回りは収まらないので、2 段目の小さな曲線で丸める。
            var raw = new List<Vector3> { new(0, 0, -5), new(6, 0, -5), new(6.6f, 0, -4.4f), new(6.6f, 0, -3.5f), new(7.2f, 0, -2.6f), new(14, 0, -2.6f), new(16, 0, -2.6f) };
            var q = new FakeQueries { Mesh = FakeQueries.Building };
            var r = await new RouteProcessor(q).ProcessAsync(Request(), raw, default);
            Assert(r.Disposition == RouteDisposition.Refined && r.Path[0] == raw[0] && r.Path[^2] == raw[^2] && r.Path[^1] == raw[^1]);
            Assert(LargestTurn(r.Path.Take(r.Path.Count - 1).ToList()) < 30, "短い辺の角が残っている: " + LargestTurn(r.Path));
            Assert(Geometry.Length(r.Path) <= Geometry.Length(raw) * 1.05f);
        });
        await Test("Shape ground without a walkable surface keeps the official corner", async () => {
            var q = new FakeQueries(); var r = await new RouteProcessor(q).ProcessAsync(Request(), Corners, default);
            Assert(r.Disposition == RouteDisposition.Original && r.Path.SequenceEqual(Corners));
        });
        await Test("Shape flight in open sky shortcuts the corner (checked line)", async () => {
            var raw = new List<Vector3> { new(0, 50, 0), new(60, 50, 0), new(60, 50, 60), new(60, 50, 70) };
            var r = await new RouteProcessor(new FakeQueries()).ProcessAsync(Request(true, settings: new(Flight: true)), raw, default);
            Assert(r.Disposition == RouteDisposition.Refined && !r.Path.Contains(raw[1]) && Geometry.Length(r.Path) < Geometry.Length(raw));
            Assert(r.Path[0] == raw[0] && r.Path[^2] == raw[2] && r.Path[^1] == raw[3]);
        });
        await Test("Shape flight curve rounds a right-angle around a rock inside the corner", async () => {
            // 角の内側に岩（x,z 15〜45）。斜めの近道は当たるので、角の近くを円弧で回る。
            static bool Rock(Segment s) => Enumerable.Range(0, 41).Select(i => Vector3.Lerp(s.From, s.To, i / 40f))
                .Any(p => p.X is > 15 and < 45 && p.Z is > 15 and < 45);
            var raw = new List<Vector3> { new(0, 50, 0), new(60, 50, 0), new(60, 50, 60), new(60, 50, 70) };
            var q = new FakeQueries { Probe = s => Rock(s) ? new(Verdict.Blocked, "rock", EvidenceKind.Collision, Code: ProbeCode.Collision) : ProbeResult.Clear };
            var r = await new RouteProcessor(q).ProcessAsync(Request(true, settings: new(Flight: true)), raw, default);
            Assert(r.Disposition == RouteDisposition.Refined && !r.Path.Contains(raw[1]) && LargestTurn(r.Path) < 30);
            Assert(Geometry.Lines(r.Path, true).All(s => !Rock(s)), "岩を通った");
            Assert(r.Path[0] == raw[0] && r.Path[^2] == raw[2] && r.Path[^1] == raw[3]);
        });
        await Test("U15 flight collision on new lines keeps the official corner", async () => {
            var raw = new List<Vector3> { new(0, 50, 0), new(60, 50, 0), new(60, 50, 60), new(60, 50, 70) };
            var q = new FakeQueries { Probe = s => OnLines(raw, s) ? ProbeResult.Clear : new(Verdict.Blocked, "rock", EvidenceKind.Collision, Code: ProbeCode.Collision) };
            var r = await new RouteProcessor(q).ProcessAsync(Request(true, settings: new(Flight: true)), raw, default);
            Assert(r.Disposition == RouteDisposition.Original && r.Path.SequenceEqual(raw));
        });
        await Test("U17 detour candidates with unverified lines are not adopted", async () => {
            var raw = new List<Vector3> { Vector3.Zero, new(10, 0, 0) };
            var q = new FakeQueries { Probe = s => Wall(s).Verdict == Verdict.Blocked ? Wall(s) : ProbeResult.Unknown("unloaded", ProbeCode.Unloaded) };
            var r = await new RouteProcessor(q).ProcessAsync(Request(), raw, default);
            Assert(r.Disposition == RouteDisposition.NoSafeCandidate && r.Path.SequenceEqual(raw) && q.Searches > 0);
        });
        await Test("U17 flight new lines that cannot be verified are not adopted", async () => {
            var raw = new List<Vector3> { new(0, 50, 0), new(60, 50, 0), new(60, 50, 60), new(60, 50, 70) };
            var q = new FakeQueries { Probe = s => OnLines(raw, s) ? ProbeResult.Clear : ProbeResult.Unknown("unloaded", ProbeCode.Unloaded) };
            var r = await new RouteProcessor(q).ProcessAsync(Request(true, settings: new(Flight: true)), raw, default);
            Assert(r.Disposition == RouteDisposition.Original && r.Path.SequenceEqual(raw));
        });
        await Test("U15 inside-corner collision rejects new curve", async () => {
            var q = new FakeQueries { Probe = s => IsOriginalLine(s) ? ProbeResult.Clear : new(Verdict.Blocked, "wall", EvidenceKind.Collision) };
            var r = await new RouteProcessor(q).ProcessAsync(Request(), Corners, default);
            Assert(r.Disposition == RouteDisposition.Original && r.Path.SequenceEqual(Corners));
        });
        await Test("U17 U18 unknown new lines are not adopted", async () => {
            var q = new FakeQueries { Probe = _ => ProbeResult.Unknown("unloaded") };
            var r = await new RouteProcessor(q).ProcessAsync(Request(), Corners, default);
            Assert(r.Disposition == RouteDisposition.Original && r.UnknownSegments == 4 && q.Searches == 0);
        });
        await Test("U17 mesh disagreement is not a reliable body collision", async () => {
            var q = new FakeQueries { Probe = _ => new(Verdict.Blocked, "mesh mismatch", EvidenceKind.Mesh) };
            var r = await new RouteProcessor(q).ProcessAsync(Request(), Corners, default);
            Assert(!r.OriginalBlocked && r.Disposition == RouteDisposition.Original && q.Searches == 0);
        });
        await Test("U11 U12 combined marker roundtrip and ground count", SyncTask(() => {
            var raw = new List<Vector3> { A, B, C, D, E, C };
            var d = GbrPathCodec.Decode(raw, true, true)!;
            Assert(d.Parts.Count == 2 && d.Landing == C && d.Encode().SequenceEqual(raw));
            d.Parts[0].Points.Insert(1, new(4, 0, 0));
            var path = d.Encode(); path.RemoveAt(path.Count - 1);
            Assert(path.Count - path.FindLastIndex(p => p == C) == 3 && path[^1] == E);
        }));
        await Test("U11 ambiguous or missing landing marker is rejected", SyncTask(() => {
            Assert(GbrPathCodec.Decode([A, C, C, D, C], true, true) == null);
            Assert(GbrPathCodec.Decode([A, B, C, D], true, true) == null);
        }));
        await Test("U12 flight and ground refinement preserve true endpoint and marker", async () => {
            var raw = Corners.Concat(new[] { new Vector3(18, 0, 14), new Vector3(22, 0, 14), C }).ToList();
            var r = await new RouteProcessor(new FakeQueries()).ProcessAsync(Request(true, true, new(Flight: true)), raw, default);
            Assert(r.Path[^1] == C && r.Path[^2] == new Vector3(22, 0, 14));
            Assert(GbrPathCodec.Decode(r.Path, true, true) != null);
        });
        await Test("U14 mounting routes keep the take-off prefix and round later corners", async () => {
            // GBR は乗り終えたら今の位置から水平 4m 以内の点を飛ばす。出発から 8m までは公式のまま残す。
            var raw = new List<Vector3> { new(0, 10, 0), new(3, 12, 0), new(20, 30, 0), new(80, 30, 0), new(80, 30, 60), new(80, 30, 70) };
            var r = await new RouteProcessor(new FakeQueries()).ProcessAsync(Request(true, settings: new(Flight: true)) with { Mounting = true }, raw, default);
            Assert(r.Disposition == RouteDisposition.Refined, r.Reason);
            Assert(r.Path.Take(3).SequenceEqual(raw.Take(3)), "離陸の区間が変わった");
            Assert(!r.Path.Contains(raw[3]) && r.Path[^2] == raw[4] && r.Path[^1] == raw[5]);
            Assert(RouteProcessor.MountKeepCount(raw, 8) == 2);
        });
        await Test("U01 unsupported flight stays original", async () => {
            var q = new FakeQueries(); var r = await new RouteProcessor(q).ProcessAsync(Request(true), Corners, default);
            Assert(r.Disposition == RouteDisposition.Original && q.Checks == 0);
        });
        await Test("U20 U44 bounded official midpoint detour avoids analytic obstacle", async () => {
            var q = new FakeQueries { Probe = Wall };
            var raw = new List<Vector3> { Vector3.Zero, new(10, 0, 0) };
            var r = await new RouteProcessor(q).ProcessAsync(Request(), raw, default);
            Assert(r.Disposition == RouteDisposition.Detour && r.Searches == 3 && q.Searches == 3);
            Assert(r.Path[0] == raw[0] && r.Path[^1] == raw[^1]);
            Assert(Geometry.Lines(r.Path, false).All(s => Wall(s).Verdict == Verdict.Clear));
            Assert(Geometry.Length(r.Path) > Geometry.Length(raw) * 1.05f, "detour must not use smoothing ratio");
        });
        await Test("U20 repeated blocked search cannot loop", async () => {
            var q = new FakeQueries { Probe = _ => new(Verdict.Blocked, "wall", EvidenceKind.Collision), Search = (_, _, _) => [Vector3.Zero, new(10, 0, 0)] };
            var r = await new RouteProcessor(q).ProcessAsync(Request(), [Vector3.Zero, new(10, 0, 0)], default);
            Assert(r.Disposition == RouteDisposition.NoSafeCandidate && q.Searches <= 5 && r.OriginalBlocked);
        });
        await Test("U21 bent blocked route still rechecked regardless of start-end chord", async () => {
            var q = new FakeQueries { Probe = Wall, Search = (_, _, _) => [new(0, 0, 3), new(5, 0, 0), new(10, 0, 3)] };
            var r = await new RouteProcessor(q).ProcessAsync(Request(), [new(0, 0, 3), new(5, 0, 0), new(10, 0, 3)], default);
            Assert(r.Disposition == RouteDisposition.NoSafeCandidate && q.Searches <= 5);
        });
        await Test("U16 ground slope follows mesh rather than flight chord", SyncTask(() => {
            var calls = 0;
            var probe = new GroundSegmentProbe((p, _, _) => new(p.X, p.X * .2f, p.Z), (a, b) => { calls++; Assert(Math.Abs(b.Y - b.X * .2f) < .001f); return ProbeResult.Clear; });
            Assert(probe.Check(Vector3.Zero, new(2, .4f, 0), 1000).Verdict == Verdict.Clear && calls == 8);
        }));
        await Test("U16 cliff and wrong floor rejected without body collision claim", SyncTask(() => {
            var probe = new GroundSegmentProbe((p, _, _) => p.X < 1 ? p : null, (_, _) => ProbeResult.Clear);
            var result = probe.Check(Vector3.Zero, new(2, 0, 0), 1000);
            Assert(result.Verdict == Verdict.Blocked && !result.ReliableBlock);
            probe = new((p, _, _) => new(p.X, 0, p.Z), (_, _) => ProbeResult.Clear);
            Assert(probe.Check(Vector3.Zero, new(2, 4, 0), 1000).Verdict == Verdict.Blocked);
        }));
        await Test("U18 ground query expiration is Unknown", SyncTask(() => {
            var probe = new GroundSegmentProbe((p, _, _) => p, (_, _) => ProbeResult.Clear);
            Assert(probe.Check(Vector3.Zero, new(2, 0, 0), -1).Verdict == Verdict.Unknown);
        }));
        await TicketTests();
        await BridgeTests();
        await LandingRegressionTests();
        await Test("U19 U20 progress excludes waits and keeps one retry across generations", SyncTask(() => {
            var monitor = new ProgressWatch(); var r = Request();
            Assert(monitor.Observe(r, A, 1000, 4, true) == ProgressDecision.None);
            Assert(monitor.Observe(r, A, 5000, 4, false) == ProgressDecision.None);
            Assert(monitor.Observe(r, A, 5001, 4, true) == ProgressDecision.None);
            Assert(monitor.Observe(r, A, 9000, 4, true) == ProgressDecision.Replan);
            monitor.ResetTracking();
            Assert(monitor.Observe(r with { Generation = 2, Id = 2 }, A, 10000, 4, true) == ProgressDecision.None);
            Assert(monitor.Observe(r with { Generation = 2, Id = 2 }, A, 14000, 4, true) == ProgressDecision.Exhausted);
        }));
        await Test("U06 navigation target identity does not require gathering UI state", SyncTask(() => {
            var auto = new FakeAuto(); Assert(GbrRequestIdentity.MatchesRequestedItem(auto, 42));
            auto.CurrentRequestedTarget = new(43); Assert(!GbrRequestIdentity.MatchesRequestedItem(auto, 42));
            auto.CurrentRequestedTarget = null; Assert(!GbrRequestIdentity.MatchesRequestedItem(auto, 42));
        }));
        await Test("U29 trace writes JSONL", async () => {
            var directory = Path.Combine(AppContext.BaseDirectory, "trace-test-" + Guid.NewGuid().ToString("N"));
            var writer = new NavigationTraceWriter(directory);
            writer.Record("test", Request(), new { Path = Corners.ToArray() }); writer.Dispose(); await writer.Completion;
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(writer.Path));
            Assert(document.RootElement.GetProperty("Kind").GetString() == "test");
        });
        await Test("U29 trace keeps writing at the file limit by deleting the oldest files", async () => {
            // 前の版は 16 ファイルで書くのをやめ、以後の記録が残らなかった（実機 10-12 09:00 以降）。
            var directory = Path.Combine(AppContext.BaseDirectory, "trace-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var old = DateTime.UtcNow.AddHours(-2);
            for (var i = 0; i < 16; i++)
            {
                var file = Path.Combine(directory, $"navigation-placeholder-{i:00}.jsonl");
                File.WriteAllText(file, ""); File.SetLastWriteTimeUtc(file, old.AddMinutes(i));
            }
            var unrelated = Path.Combine(directory, "keep-me.txt"); File.WriteAllText(unrelated, "x");
            var writer = new NavigationTraceWriter(directory);
            writer.Record("after-limit", null); writer.Dispose(); await writer.Completion;
            var files = Directory.GetFiles(directory, "navigation-*.jsonl");
            Assert(writer.Error.Length == 0 && files.Length == 16, "数が上限のまま書き続ける");
            Assert(!File.Exists(Path.Combine(directory, "navigation-placeholder-00.jsonl")), "一番古いファイルを消す");
            Assert(File.Exists(Path.Combine(directory, "navigation-placeholder-01.jsonl")) && File.Exists(unrelated), "消すのは 1 つだけ・記録以外は消さない");
            Assert(File.ReadAllText(writer.Path).Contains("after-limit"));
        });
        await Test("U29 trace rotates by size and keeps only the newest files", async () => {
            var directory = Path.Combine(AppContext.BaseDirectory, "trace-test-" + Guid.NewGuid().ToString("N"));
            var writer = new NavigationTraceWriter(directory, maxFiles: 3, maxFileBytes: 2048);
            // 待ち行列は 128 件（溢れた分は捨てる作り）なので、それより少なく書く。
            for (var i = 0; i < 100; i++) writer.Record("entry", null, new { Index = i, Pad = new string('x', 100) });
            writer.Dispose(); await writer.Completion;
            var files = Directory.GetFiles(directory, "navigation-*.jsonl");
            Assert(writer.Error.Length == 0 && files.Length == 3 && writer.Deleted > 0, $"files={files.Length} deleted={writer.Deleted}");
            Assert(files.All(f => new FileInfo(f).Length <= 2048), "1 ファイルの大きさの上限");
            var text = File.ReadAllText(writer.Path);
            Assert(text.Contains("\"Index\":99"), "最新の記録が残る");
            Assert(!files.Any(f => File.ReadAllText(f).Contains("\"Index\":0,")), "最古の記録は上書きされる");
            Assert(writer.Dropped == 0);
        });
        await Test("U29 trace skips a file another game is still writing", async () => {
            var directory = Path.Combine(AppContext.BaseDirectory, "trace-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var busy = Path.Combine(directory, "navigation-busy.jsonl"); var older = Path.Combine(directory, "navigation-older.jsonl");
            File.WriteAllText(busy, ""); File.WriteAllText(older, "");
            File.SetLastWriteTimeUtc(busy, DateTime.UtcNow.AddHours(-3)); File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-2));
            using (new FileStream(busy, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                var writer = new NavigationTraceWriter(directory, maxFiles: 2);
                writer.Record("x", null); writer.Dispose(); await writer.Completion;
                Assert(writer.Error.Length == 0 && File.Exists(busy) && !File.Exists(older), "書いている途中のファイルは飛ばして次に古いものを消す");
            }
        });
        await Test("U27 U28 acquisition UI-only, null and uncontrolled deltas stay Unknown", SyncTask(() => {
            var observer = new GatherAcquisitionObserver(); observer.Begin(1, 42, null);
            Assert(observer.Sample(2, true, true)!.Classification == "AcquisitionUnknown");
            observer.Begin(2, 42, 0);
            Assert(observer.Sample(2, false, true)!.Classification == "AcquisitionUnknown");
            Assert(observer.Sample(2, true, false)!.Classification == "AcquisitionUnknown");
            Assert(observer.Sample(2, true, true)!.Classification == "AcquisitionUnknown", "do not retroactively relabel uncontrolled acquisition");
            observer.Begin(3, 42, 0);
            Assert(observer.Sample(2, true, true)!.Classification == "ControlledInventoryDelta");
            Assert(observer.Sample(2, true, true)!.OperationsObserved == 1);
        }));
        await FlightTests();
        Console.WriteLine($"RESULT {passed} PASS / {failed} FAIL");
        return failed == 0 ? 0 : 1;
    }
    private static Func<Task> SyncTask(Action action) => () => Sync(action);
    // FakeQueries.Building の塊（x<8 かつ z>0）の中か。縁の上（誤差 1cm）は中に数えない。
    private static bool InsideBuilding(Vector3 p) => p.X < 8 - .01f && p.Z > .01f;
    // 点の並びの中で一番大きい曲がり（度・水平）。
    private static double LargestTurn(IReadOnlyList<Vector3> path)
    {
        var largest = 0d;
        for (var i = 1; i < path.Count - 1; i++)
        {
            var a = path[i] - path[i - 1]; var b = path[i + 1] - path[i]; a.Y = b.Y = 0;
            if (a.LengthSquared() < 1e-6f || b.LengthSquared() < 1e-6f) continue;
            largest = Math.Max(largest, Math.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1, 1)) * 180 / Math.PI);
        }
        return largest;
    }
    // 線が元の折れ線のどれかの区間の上に乗っているか。
    private static bool OnLines(IReadOnlyList<Vector3> raw, Segment s)
    {
        static float Distance(Vector3 p, Vector3 a, Vector3 b)
        {
            var d = b - a; var t = Math.Clamp(Vector3.Dot(p - a, d) / d.LengthSquared(), 0, 1);
            return Vector3.Distance(p, a + d * t);
        }
        for (var i = 1; i < raw.Count; i++)
            if (Distance(s.From, raw[i - 1], raw[i]) < .01f && Distance(s.To, raw[i - 1], raw[i]) < .01f) return true;
        return false;
    }
    private static bool IsOriginalLine(Segment s) => (s.From.Z == 0 && s.To.Z == 0)
        || (s.From.X == 8 && s.To.X == 8) || (s.From.Z == 8 && s.To.Z == 8);
    // Independent analytical slab intersection for a wall box: x 4..6, z -1..1.
    private static ProbeResult Wall(Segment s)
    {
        var delta = s.To - s.From; var lo = 0f; var hi = 1f;
        foreach (var axis in new[] { 0, 2 })
        {
            var min = axis == 0 ? 4f : -1f; var max = axis == 0 ? 6f : 1f;
            if (Math.Abs(delta[axis]) < .000001f) { if (s.From[axis] < min || s.From[axis] > max) return ProbeResult.Clear; continue; }
            var a = (min - s.From[axis]) / delta[axis]; var b = (max - s.From[axis]) / delta[axis];
            lo = Math.Max(lo, Math.Min(a, b)); hi = Math.Min(hi, Math.Max(a, b));
            if (lo > hi) return ProbeResult.Clear;
        }
        return new(Verdict.Blocked, "analytic wall", EvidenceKind.Collision, new(5, 0, 0));
    }
    private static async Task TicketTests()
    {
        await Test("U04 processing remains pending after original completion", async () => {
            var pending = new TaskCompletionSource<RouteResult>(); using var cts = new CancellationTokenSource();
            var ticket = new RouteTicket(Request(), Task.FromResult(Corners), cts, (_, _, _) => pending.Task, false);
            Assert(!ticket.Proxy.IsCompleted && ticket.HoldingRecovery);
            pending.SetResult(new(RouteDisposition.Refined, Corners, "ok"));
            Assert((await ticket.Proxy).SequenceEqual(Corners));
        });
        await Test("U36 helper exception returns original, not fault or empty", async () => {
            using var cts = new CancellationTokenSource();
            var ticket = new RouteTicket(Request(), Task.FromResult(Corners), cts, (_, _, _) => throw new Exception("injected"), false);
            Assert((await ticket.Proxy).SequenceEqual(Corners) && ticket.Proxy.IsCompletedSuccessfully);
        });
        await Test("U05 U37 invalidation cannot complete before unfinished original", async () => {
            var original = new TaskCompletionSource<List<Vector3>>(); using var cts = new CancellationTokenSource(); var processed = 0;
            var ticket = new RouteTicket(Request(), original.Task, cts, (_, _, _) => { processed++; throw new Exception(); }, false);
            ticket.Invalidate(); Assert(!ticket.Proxy.IsCompleted);
            original.SetResult(Corners);
            Assert((await ticket.Proxy).SequenceEqual(Corners) && processed == 0 && !cts.IsCancellationRequested);
        });
        await Test("U05 OFF during processing rejects late refinement", async () => {
            var pending = new TaskCompletionSource<RouteResult>(); using var cts = new CancellationTokenSource();
            var ticket = new RouteTicket(Request(), Task.FromResult(Corners), cts, (_, _, _) => pending.Task, false);
            ticket.Invalidate(); var original = await ticket.Proxy;
            pending.SetResult(new(RouteDisposition.Refined, [Vector3.Zero], "late"));
            Assert(original.SequenceEqual(Corners) && !ticket.Valid);
        });
        await Test("U07 true original failure preserved", async () => {
            using var cts = new CancellationTokenSource();
            var ticket = new RouteTicket(Request(), Task.FromException<List<Vector3>>(new FormatException("original")), cts, (_, _, _) => throw new Exception(), false);
            try { await ticket.Proxy; throw new Exception("failure swallowed"); } catch (FormatException) { }
        });
        await Test("U07 true original cancellation preserved", async () => {
            using var cts = new CancellationTokenSource();
            var ticket = new RouteTicket(Request(), Task.FromCanceled<List<Vector3>>(new CancellationToken(true)), cts, (_, _, _) => throw new Exception(), false);
            try { await ticket.Proxy; throw new Exception("cancel swallowed"); } catch (OperationCanceledException) { }
        });
        await Test("U34 complete tuple preserved on proxy update", SyncTask(() => {
            var auto = new FakeAuto(); using var c = new CancellationTokenSource(); var original = Task.FromResult(Corners); auto.Seed(original, c);
            var a = new NavStateAccessor(typeof(FakeAuto)); var before = a.Read(auto); var replacement = Task.FromResult(new List<Vector3>());
            Assert(a.Replace(auto, original, c, replacement)); var after = a.Read(auto);
            Assert(after == before with { Task = replacement });
        }));
        await Test("U38 stale restoration cannot overwrite a new task or CTS", SyncTask(() => {
            var auto = new FakeAuto(); using var c = new CancellationTokenSource(); using var other = new CancellationTokenSource();
            var original = Task.FromResult(Corners); auto.Seed(original, c); var a = new NavStateAccessor(typeof(FakeAuto));
            Assert(!a.Replace(auto, Task.FromResult(Corners), c, Task.FromResult(Corners)));
            Assert(!a.Replace(auto, original, other, Task.FromResult(Corners)));
            Assert(ReferenceEquals(a.Read(auto).Task, original));
        }));
    }
    private static async Task BridgeTests()
    {
        await Test("U02 U03 managed bridge passes one request once; exact original dispatch", async () => {
            var auto = new FakeAuto(); var env = new FakeEnvironment(); var q = new FakeQueries();
            using var bridge = new GbrNavigationBridge(auto, env, new(q));
            Assert(bridge.HookCount == 7);
            auto.DoNodeMovement(new(new(42)), new()); auto.DoNodeMovement(new(new(42)), new());
            var snapshot = new NavStateAccessor(typeof(FakeAuto)).Read(auto);
            Assert(!ReferenceEquals(snapshot.Task, auto.Next)); await snapshot.Task!;
            auto.HandlePathfinding();
            Assert(env.Attempts == 1 && auto.Moves == 1 && auto.Dispatched!.SequenceEqual(auto.Next.Result) && q.Checks == 0);
            var consumed = env.Events.Single(e => e.Kind == "consumed").Detail!;
            var captured = (Vector3[])consumed.GetType().GetProperty("Path")!.GetValue(consumed)!;
            var first = captured[0]; auto.Dispatched![0] = new(900, 900, 900); auto.Next.Result[0] = new(800, 800, 800);
            Assert(captured[0] == first, "logged path must be an independent snapshot");
        });
        await Test("U01 OFF and fishing scope do not wrap tasks or query", SyncTask(() => {
            foreach (var off in new[] { false, true })
            {
                var auto = new FakeAuto(); var env = new FakeEnvironment { Mode = off ? NavigationMode.Off : NavigationMode.Observe }; var q = new FakeQueries();
                using var bridge = new GbrNavigationBridge(auto, env, new(q));
                auto.DoNodeMovement(new(off ? new(42) : null), new());
                Assert(ReferenceEquals(new NavStateAccessor(typeof(FakeAuto)).Read(auto).Task, auto.Next) && env.Attempts == 0 && q.Checks == 0);
            }
        }));
        await Test("U43 null node id still belongs to mining/botany scope", SyncTask(() => {
            var auto = new FakeAuto(); var env = new FakeEnvironment();
            using var bridge = new GbrNavigationBridge(auto, env, new(new FakeQueries()));
            auto.DoNodeMovement(new(new(42)), new()); Assert(env.Attempts == 1);
        }));
        await Test("U33 direct, initial, retry and Handle fallback all intercepted", async () => {
            foreach (var stage in new[] { 0, 1, 2, 3 })
            {
                var auto = new FakeAuto { Fly = stage != 0, Direct = stage == 0 };
                if (stage == 2) auto.SeedRetry();
                auto.Next = Task.FromResult(stage == 3 ? new List<Vector3>() : [A, B, C, D, C]);
                var env = new FakeEnvironment(); using var bridge = new GbrNavigationBridge(auto, env, new(new FakeQueries()));
                auto.DoNodeMovement(new(new(42)), new());
                await new NavStateAccessor(typeof(FakeAuto)).Read(auto).Task!;
                auto.HandlePathfinding();
                if (stage == 3) { Assert(env.Attempts == 2); await new NavStateAccessor(typeof(FakeAuto)).Read(auto).Task!; auto.HandlePathfinding(); }
                Assert(auto.Moves == 1 && auto.Forces == 0);
            }
        });
        await Test("U35 original exception is not retried and scope unwinds", SyncTask(() => {
            var auto = new FakeAuto { ThrowAfterNavigate = true }; var env = new FakeEnvironment();
            using var bridge = new GbrNavigationBridge(auto, env, new(new FakeQueries()));
            try { auto.DoNodeMovement(new(new(42)), new()); } catch (InvalidOperationException) { }
            Assert(auto.NavigateCalls == 1);
            auto.ThrowAfterNavigate = false; auto.StopNavigation(); auto.Navigate(auto.Destination, false, true, null);
            Assert(env.Attempts == 0);
        }));
        await Test("U37 StopPathfinding restores original before CTS disposal subscription", async () => {
            var original = new TaskCompletionSource<List<Vector3>>(); var auto = new FakeAuto { Next = original.Task }; var env = new FakeEnvironment();
            using var bridge = new GbrNavigationBridge(auto, env, new(new FakeQueries()));
            auto.DoNodeMovement(new(new(42)), new()); var cts = auto.LastCancellation!;
            auto.StopPathfinding(); Assert(cts.IsCancellationRequested); _ = cts.Token;
            original.SetResult(Corners); await Task.Delay(30);
            try { _ = cts.Token; throw new Exception("original CTS not disposed"); } catch (ObjectDisposedException) { }
            Assert(auto.Forces == 0 && auto.Moves == 0);
        });
        await Test("U30 U42 detach restores in-flight original and removes own hooks", SyncTask(() => {
            var original = new TaskCompletionSource<List<Vector3>>(); var auto = new FakeAuto { Next = original.Task }; var env = new FakeEnvironment();
            var bridge = new GbrNavigationBridge(auto, env, new(new FakeQueries())); auto.DoNodeMovement(new(new(42)), new()); bridge.Dispose();
            Assert(bridge.HookCount == 0 && ReferenceEquals(new NavStateAccessor(typeof(FakeAuto)).Read(auto).Task, original.Task));
            original.SetResult(Corners); auto.HandlePathfinding(); Assert(auto.Moves == 1);
        }));
        await Test("U06 target or territory changes reject pending result", async () => {
            // 加工が終わって曲線の経路ができていても、対象が変わったら使わない（加工の終わりを待ってから確かめる＝時間に左右されない）。
            var auto = new FakeAuto { Next = Task.FromResult(Corners) }; var env = new FakeEnvironment { Mode = NavigationMode.Improve };
            using var bridge = new GbrNavigationBridge(auto, env, new(new FakeQueries { Mesh = FakeQueries.Building }));
            auto.DoNodeMovement(new(new(42)), new());
            var proxy = new NavStateAccessor(typeof(FakeAuto)).Read(auto).Task!;
            Assert(!(await proxy).SequenceEqual(Corners), "前提：加工で経路が変わること");
            env.Territory++;
            auto.HandlePathfinding(); Assert(auto.Dispatched!.SequenceEqual(auto.Next.Result) && auto.Forces == 0);
        });
        await Test("U42 partial hook registration rolls back and preserves another hook", SyncTask(() => {
            var auto = new FakeAuto(); var env = new FakeEnvironment(); var otherCalls = 0; var installed = 0;
            using var other = ManagedHookRuntime.CreateHook(typeof(FakeAuto).GetMethod("HandlePathfinding")!,
                (Action<Action<object>, object>)((orig, self) => { otherCalls++; orig(self); }));
            try
            {
                using var bridge = new GbrNavigationBridge(auto, env, new(new FakeQueries()), (m, d) => {
                    if (++installed == 5) throw new InvalidOperationException("injected install failure");
                    return ManagedHookRuntime.CreateHook(m, d);
                });
                throw new Exception("expected installation failure");
            }
            catch (InvalidOperationException) { }
            auto.DoNodeMovement(new(new(42)), new()); auto.HandlePathfinding();
            Assert(env.Attempts == 0 && auto.Moves == 1 && otherCalls == 1);
        }));
        await Test("U36 reliable block remains visible after unexpected processor failure", async () => {
            var request = Request(); using var cts = new CancellationTokenSource();
            var ticket = new RouteTicket(request, Task.FromResult(Corners), cts, (r, _, _) => {
                r.Evidence.MarkBlocked(); throw new Exception("after obstacle detection");
            }, false);
            await ticket.Proxy;
            Assert(ticket.Result?.Disposition == RouteDisposition.NoSafeCandidate && ticket.Result.OriginalBlocked);
        });
        await Test("U39 final connector Unknown falls back before consumption", async () => {
            var auto = new FakeAuto { Next = Task.FromResult(Corners) }; var env = new FakeEnvironment { Mode = NavigationMode.Improve, Final = ProbeResult.Unknown("wall connection") };
            using var bridge = new GbrNavigationBridge(auto, env, new(new FakeQueries())); auto.DoNodeMovement(new(new(42)), new());
            await new NavStateAccessor(typeof(FakeAuto)).Read(auto).Task!; auto.HandlePathfinding();
            Assert(auto.Dispatched!.SequenceEqual(Corners) && auto.Moves == 1 && auto.Forces == 0 && env.StopCount == 0);
        });
        await Test("U39 reliable final collision falls back to the official route without stopping", async () => {
            // 新しい接続線が当たるなら使わず、公式の経路（GBR だけのときと同じ動き）で続ける。止めない。
            var auto = new FakeAuto { Next = Task.FromResult(Corners) }; var env = new FakeEnvironment { Mode = NavigationMode.Improve,
                Final = new(Verdict.Blocked, "wall", EvidenceKind.Collision), OnStop = auto.StopNavigation };
            using var bridge = new GbrNavigationBridge(auto, env, new(new FakeQueries { Mesh = FakeQueries.Building })); auto.DoNodeMovement(new(new(42)), new());
            await new NavStateAccessor(typeof(FakeAuto)).Read(auto).Task!; auto.HandlePathfinding();
            Assert(auto.Dispatched!.SequenceEqual(Corners) && auto.Moves == 1 && auto.Forces == 0 && env.StopCount == 0);
        });
        await Test("U41 blocked official route without a detour continues on the official route", async () => {
            // 前の版はここで自動採集を止めていた（実機 10-12 06:05）。公式の線の衝突は誤判定もあるので、公式の経路で続ける。
            var q = new FakeQueries { Probe = _ => new(Verdict.Blocked, "rock", EvidenceKind.Collision, Code: ProbeCode.Collision) };
            var auto = new FakeAuto { Next = Task.FromResult(Corners) };
            var env = new FakeEnvironment { Mode = NavigationMode.Improve, OnStop = auto.StopNavigation };
            using var bridge = new GbrNavigationBridge(auto, env, new(q)); auto.DoNodeMovement(new(new(42)), new());
            await new NavStateAccessor(typeof(FakeAuto)).Read(auto).Task!; auto.HandlePathfinding();
            Assert(env.StopCount == 0 && auto.Enabled && auto.Moves == 1 && auto.Dispatched!.SequenceEqual(Corners));
            Assert(env.Events.Any(e => e.Kind == "original-fallback") && env.Movement.Current?.Code == "OriginalFallback");
        });
        await Test("U40 existing recovery already running is never taken over", SyncTask(() => {
            var auto = new FakeAuto(); auto.Recovery.IsRunning = true; var env = new FakeEnvironment();
            using var bridge = new GbrNavigationBridge(auto, env, new(new FakeQueries())); auto.DoNodeMovement(new(new(42)), new());
            Assert(env.Attempts == 0); auto.Recovery.Check(A, true); Assert(auto.Recovery.LastDestination == A && auto.Recovery.LastPathing);
        }));
        await Test("U25 U40 helper lease resets only its own recovery check and releases", async () => {
            var gate = new TaskCompletionSource();
            var queries = new FakeQueries { AsyncProbe = async (segments, cancel) => {
                await gate.Task.WaitAsync(cancel); return Enumerable.Repeat(ProbeResult.Clear, segments.Count).ToArray();
            } };
            var auto = new FakeAuto { Next = Task.FromResult(Corners) }; var env = new FakeEnvironment { Mode = NavigationMode.Improve };
            using var bridge = new GbrNavigationBridge(auto, env, new(queries)); auto.DoNodeMovement(new(new(42)), new());
            auto.Recovery.Check(A, true); Assert(auto.Recovery.LastDestination == default && !auto.Recovery.LastPathing);
            var unrelated = new FakeRecovery(); unrelated.Check(A, true); Assert(unrelated.LastDestination == A && unrelated.LastPathing);
            gate.SetResult(); await new NavStateAccessor(typeof(FakeAuto)).Read(auto).Task!;
            auto.Recovery.Check(A, true); Assert(auto.Recovery.LastDestination == A && auto.Recovery.LastPathing);
        });
        await Test("U39 unverified landing switch is left to GBR without stopping", async () => {
            var auto = new FakeAuto { Fly = true, Direct = false, Next = Task.FromResult(new List<Vector3> { A, B, C, D, E, D }) };
            var env = new FakeEnvironment { Mode = NavigationMode.Improve, Settings = new(Flight: true), OnStop = auto.StopNavigation };
            using var bridge = new GbrNavigationBridge(auto, env, new(new FakeQueries())); auto.DoNodeMovement(new(new(42)), new());
            await new NavStateAccessor(typeof(FakeAuto)).Read(auto).Task!; auto.HandlePathfinding(); Assert(auto.Moves == 1);
            auto.SeedLandingSwitch(); env.Final = ProbeResult.Unknown("descent not verified"); auto.HandlePathfinding();
            Assert(env.StopCount == 0 && auto.Forces == 0 && auto.HandleCalls == 2);
        });
        await Test("U36 non-cooperative helper work expires without faulting GBR", async () => {
            using var c = new CancellationTokenSource(); var pending = new TaskCompletionSource<RouteResult>();
            var ticket = new RouteTicket(Request(settings: new(RefineMilliseconds: 5, RerouteMilliseconds: 5)), Task.FromResult(Corners), c, (_, _, _) => pending.Task, false);
            Assert((await ticket.Proxy.WaitAsync(TimeSpan.FromSeconds(2))).SequenceEqual(Corners));
            pending.SetException(new Exception("late helper failure"));
            Assert(ticket.Result?.Disposition == RouteDisposition.Original && ticket.Proxy.IsCompletedSuccessfully);
        });
    }
}
