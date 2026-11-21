using System.Numerics;
using System.Runtime.CompilerServices;
using GBRHelper.Navigation;

namespace NavigationTests;

public sealed class FakeQueries : IRouteQueries, SmoothNav.Core.IMeshPointQuery
{
    // 曲線の地上の確かめに使う歩ける面（vnavmesh の点の照会の代わり）。null＝面が無い（地上の曲線は作られない）。
    public Func<Vector3, float, Vector3?>? Mesh;
    public int MeshQueries;
    public Vector3? NearestPoint(Vector3 point, float halfExtentXZ, float halfExtentY) { MeshQueries++; return Mesh?.Invoke(point, halfExtentXZ); }
    // 開けた平らな面（y=0）から、x<8 かつ z>0 の四角い塊（建物）を除いた面。公式の経路 A→B→C は塊の角 B で曲がる。
    public static Vector3? Building(Vector3 p, float half)
    {
        if (!(p.X < 8 && p.Z > 0)) return p with { Y = 0 };
        var toEdgeX = 8 - p.X; var toEdgeZ = p.Z;
        if (Math.Min(toEdgeX, toEdgeZ) > half) return null;
        return toEdgeX < toEdgeZ ? new(8, 0, p.Z) : new(p.X, 0, 0);
    }
    public Func<Segment, ProbeResult> Probe = _ => ProbeResult.Clear;
    public Func<Vector3, Vector3, bool, List<Vector3>> Search = (a, b, _) => [a, b];
    public int Checks, Searches, Projects;
    public Func<IReadOnlyList<Segment>, CancellationToken, Task<ProbeResult[]>>? AsyncProbe;
    public async Task<ProbeResult[]> CheckAsync(IReadOnlyList<Segment> segments, CancellationToken cancel, Action<ProbeResult>? onReliableBlock = null)
    {
        cancel.ThrowIfCancellationRequested(); Checks += segments.Count;
        var results = AsyncProbe != null ? await AsyncProbe(segments, cancel) : segments.Select(Probe).ToArray();
        foreach (var probe in results.Where(p => p.ReliableBlock)) onReliableBlock?.Invoke(probe);
        return results;
    }
    public Task<List<Vector3>> FindAsync(Vector3 from, Vector3 to, bool flying, CancellationToken cancel)
    { cancel.ThrowIfCancellationRequested(); Searches++; return Task.FromResult(Search(from, to, flying)); }
    public Task<Vector3?> ProjectAsync(Vector3 point, bool flying, CancellationToken cancel)
    { cancel.ThrowIfCancellationRequested(); Projects++; return Task.FromResult<Vector3?>(point); }
}
public sealed class FakeItem(uint id) { public uint ItemId { get; } = id; }
public readonly record struct FakeTarget(FakeItem? Gatherable);
public sealed class FakeConfig;
public enum Stage { Initial, Fallback, Retry, Done }

// Behavioral contract fixture, not GBR source and not a game substitute.
public sealed class FakeAuto
{
    private (Task<List<Vector3>>? task, CancellationTokenSource? cts, Vector3 destination, bool flying, bool mountingUp,
        bool direct, bool offset, Stage stage, long lastTry, int landWP) _navState;
    private readonly FakeRecovery _advancedUnstuck = new();
    public FakeRecovery Recovery => _advancedUnstuck;
    public Task<List<Vector3>> Next = Task.FromResult(new List<Vector3> { new(1, 0, 0), new(3, 0, 0), new(3, 0, 4), new(6, 0, 4) });
    public Task<List<Vector3>> Fallback = Task.FromResult(new List<Vector3> { new(1, 0, 0), new(5, 0, 0) });
    public int NavigateCalls, HandleCalls, Moves, Stops, Forces;
    public int LandingTransitions, Dismounts;
    public List<Vector3>? LiveRemaining;
    public List<Vector3>? Dispatched;
    public bool IsPathing { get; set; }
    public bool ThrowAfterNavigate;
    public bool Enabled { get; set; } = true;
    public CancellationTokenSource? LastCancellation;
    public Vector3 Destination = new(6, 0, 4);
    public bool Fly, Direct = true, Mounting, Offset;
    public FakeItem? CurrentRequestedTarget { get; set; } = new(42);
    public void Seed(Task<List<Vector3>> task, CancellationTokenSource cts)
        => _navState = (task, cts, new(7, 8, 9), true, true, false, true, Stage.Retry, 1234567, 23);
    public void SeedRetry() => _navState = (null, null, Destination, true, false, false, false, Stage.Retry, 123, 0);
    public void SeedLandingSwitch() => _navState = (null, null, Destination, true, false, false, false, Stage.Done, 123, 3);
    public void SeedMountSwitch() => _navState = (null, null, Destination, true, true, false, false, Stage.Done, 123, 0);
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void DoNodeMovement(FakeTarget target, FakeConfig config) => Navigate(Destination, Fly, Direct, null);
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Navigate(Vector3 destination, bool fly, bool direct, uint? node)
    {
        NavigateCalls++;
        if (_navState.task != null) return;
        var retry = _navState.stage == Stage.Retry;
        var corrected = GetCorrectedDestination(destination, Vector3.Zero, node);
        _navState = (Next, LastCancellation = new(), destination, fly, Mounting, direct, Offset,
            retry ? Stage.Retry : Stage.Initial, 888, 0);
        if (ThrowAfterNavigate) throw new InvalidOperationException("original navigate exception");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Vector3 GetCorrectedDestination(in Vector3 destination, in Vector3 player, uint? node) => destination;
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void HandlePathfinding()
    {
        HandleCalls++;
        if (_navState.flying && _navState.stage == Stage.Done && LiveRemaining is { } remaining && remaining.Count < _navState.landWP)
        {
            // Contract from installed HandlePathfinding IL_0097..IL_00c1: one complete transition.
            Dispatched = remaining.ToList(); Moves++; LandingTransitions++; Dismounts++;
            _navState.flying = false; return;
        }
        if (_navState.task == null || !_navState.task.IsCompleted) return;
        List<Vector3> path;
        try { path = _navState.task.GetAwaiter().GetResult(); }
        catch { StopNavigation(); Forces++; return; }
        _navState.task = null; _navState.cts?.Dispose(); _navState.cts = null;
        if (path.Count == 0)
        {
            if (!_navState.direct && _navState.stage == Stage.Initial)
            { _navState.stage = Stage.Fallback; _navState.task = Fallback; _navState.cts = new(); }
            else { StopNavigation(); Forces++; }
            return;
        }
        if (!_navState.direct && _navState.stage is Stage.Initial or Stage.Retry)
        {
            var marker = path[^1]; path.RemoveAt(path.Count - 1);
            _navState.landWP = path.Count - path.FindLastIndex(p => p == marker); _navState.stage = Stage.Done;
        }
        if (IsPathing) path = GbrPathCodec.ForDispatch(path, false, Vector3.Zero, true);
        Dispatched = path.ToList(); Moves++;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void StopPathfinding()
    {
        if (_navState.cts != null && _navState.task != null)
        {
            var c = _navState.cts; c.Cancel();
            _ = _navState.task.ContinueWith(_ => c.Dispose(), TaskScheduler.Default);
            _navState.task = null; _navState.cts = null;
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void StopNavigation() { StopPathfinding(); _navState = default; Stops++; }
}
public sealed class FakeRecovery
{
    public bool IsRunning { get; set; }
    public Vector3 LastDestination;
    public bool LastPathing;
    public int Calls;
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool Check(Vector3 destination, bool pathing) { LastDestination = destination; LastPathing = pathing; Calls++; return !IsRunning; }
}
public sealed class FakeEnvironment : IBridgeEnvironment
{
    public bool MayProcess { get; set; } = true;
    public bool Current = true;
    public bool IsCurrent(RouteRequest request) => Current && request.Territory == Territory;
    public bool IsFlying { get; set; }
    public bool IsMounted { get; set; }
    public bool IsDiving { get; set; }
    public uint Territory { get; set; } = 123;
    public Vector3 Position { get; set; }
    public NavigationMode Mode { get; set; } = NavigationMode.Observe;
    public RouteSettings Settings { get; set; } = new();
    public bool CanMonitorProgress { get; set; } = true;
    public double StallThresholdSeconds { get; set; } = 5;
    public int StopCount, Attempts;
    public List<(string Kind, RouteRequest? Request, object? Detail)> Events = new();
    public ProbeResult Final = ProbeResult.Clear;
    public float PathTolerance { get; set; } = .25f;
    public List<Vector3>? Remaining = [new(1, 0, 0), new(3, 0, 0)];
    public MovementDiagnostics Movement = new();
    public Action? OnStop;
    public bool ThrowDiagnostics, ThrowStop;
    public string NodeIdentity(Vector3 destination, uint? nodeId) => "node";
    public List<Vector3>? RemainingWaypoints() => Remaining;
    public Func<Vector3, Vector3, bool, ProbeResult>? FinalProbe;
    public ProbeResult FinalConnection(Vector3 from, Vector3 to, bool fly) => FinalProbe?.Invoke(from, to, fly) ?? Final;
    public bool StopGather(RouteRequest request, string reason) { StopCount++; if (ThrowStop) throw new IOException("stop failed"); OnStop?.Invoke(); Movement.CompleteStop(request, "stopped"); Record("stop", request, reason); return true; }
    public void ReportMovement(MovementSnapshot snapshot, bool failure) { Movement.Record(snapshot, failure); if (ThrowDiagnostics) throw new IOException("disk full"); Record("movement", snapshot.Request, snapshot); }
    public void RecordStopResult(RouteRequest request, string result) => Movement.CompleteStop(request, result);
    public void Record(string kind, RouteRequest? request, object? detail = null) => Events.Add((kind, request, detail));
    public void ObserveAttempt(RouteRequest request) => Attempts++;
}
