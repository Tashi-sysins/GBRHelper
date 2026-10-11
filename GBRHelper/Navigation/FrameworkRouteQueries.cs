using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace GBRHelper.Navigation;

/// <summary>Bounded game-thread queries. Cancel only our own searches; never use CancelAll.</summary>
public sealed class FrameworkRouteQueries : IRouteQueries, SmoothNav.Core.IMeshPointQuery, IDisposable
{
    private readonly ConcurrentQueue<Action> queue = new();
    private readonly object gate = new();
    private readonly NativeCollisionProbe native = new();
    private readonly GroundSegmentProbe ground;
    private readonly Func<Vector3, float, float, Vector3?> nearest;
    private readonly Func<bool> ready;
    private int pending, searches;
    private bool disposed;
    public int Pending => Volatile.Read(ref pending);
    public int Searches => Volatile.Read(ref searches);
    public double LastPumpMilliseconds { get; private set; }
    public object NativeMetrics => native.Metrics;
    public FrameworkRouteQueries()
    {
        var mesh = Svc.PluginInterface.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint");
        var readiness = Svc.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        nearest = mesh.InvokeFunc; ready = readiness.InvokeFunc;
        ground = new(nearest, native.GroundBody);
    }

    public ProbeResult CheckNow(Vector3 from, Vector3 to, bool flying)
    {
        try
        {
            if (!Svc.Framework.IsInFrameworkUpdateThread || disposed || !ready()) return ProbeResult.Unknown("メッシュまたはスレッドが未準備", ProbeCode.NotReady);
            return flying ? native.Flight(from, to) : ground.Check(from, to);
        }
        catch (Exception ex) { return ProbeResult.Unknown("照会例外: " + ex.GetType().Name, ProbeCode.QueryException); }
    }
    private Task<T> Enqueue<T>(Func<T> action, CancellationToken token)
    {
        lock (gate)
        {
        if (disposed || pending >= 512) return Task.FromException<T>(new InvalidOperationException("照会キュー上限または解除済み"));
        Interlocked.Increment(ref pending);
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = result.Task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        queue.Enqueue(() =>
        {
            Interlocked.Decrement(ref pending);
            if (disposed || token.IsCancellationRequested) { result.TrySetCanceled(); return; }
            try { result.TrySetResult(action()); }
            catch (Exception ex) { result.TrySetException(ex); }
        });
        return result.Task.WaitAsync(token);
        }
    }
    public async Task<ProbeResult[]> CheckAsync(IReadOnlyList<Segment> segments, CancellationToken cancel, Action<ProbeResult>? onReliableBlock = null)
    {
        // Bound each batch so a long path cannot fill the queue permanently.
        var result = new List<ProbeResult>();
        for (var i = 0; i < segments.Count; i += 32)
        {
            cancel.ThrowIfCancellationRequested();
            var batch = segments.Skip(i).Take(32).Select(s => CheckSegmentAsync(s, cancel, onReliableBlock)).ToArray();
            result.AddRange(await Task.WhenAll(batch).ConfigureAwait(false));
        }
        return result.ToArray();
    }
    private async Task<ProbeResult> CheckSegmentAsync(Segment segment, CancellationToken cancel, Action<ProbeResult>? onReliableBlock)
    {
        ProbeResult Observe(ProbeResult probe) { if (probe.ReliableBlock) onReliableBlock?.Invoke(probe); return probe; }
        if (segment.Flying) return await Enqueue(() => Observe(CheckNow(segment.From, segment.To, true)), cancel).ConfigureAwait(false);
        var cursor = ground.Begin(segment.From, segment.To);
        if (cursor.Complete) return cursor.Advance();
        long? stamp = null;
        for (var slice = 0; slice < 256; slice++)
        {
            var result = await Enqueue(() =>
            {
                if (!Svc.Framework.IsInFrameworkUpdateThread || !ready()) return ProbeResult.Unknown("メッシュまたはスレッドが未準備", ProbeCode.NotReady);
                var current = native.CoverageStamp;
                if (current == null) return ProbeResult.Unknown("当たり判定の読込状態を確認できません", native.CoverageCode);
                if (stamp.HasValue && stamp != current) return ProbeResult.Unknown("分割検査中に当たり判定が変わりました", ProbeCode.Unloaded);
                stamp = current;
                // A slice may overrun by one non-preemptible native/IPC operation; never block awaiting another frame.
                return Observe(cursor.Advance(.75, 32));
            }, cancel).ConfigureAwait(false);
            if (cursor.Complete || result.Code != ProbeCode.TimeBudget) return result;
            // Completed samples remain in this request-local cursor; the original task's total deadline still applies.
        }
        return ProbeResult.Unknown("地上の分割検査回数上限", ProbeCode.Limit);
    }
    public async Task<List<Vector3>> FindAsync(Vector3 from, Vector3 to, bool flying, CancellationToken cancel)
    {
        var task = await Enqueue(() =>
        {
            if (!ready() || searches >= 1) throw new InvalidOperationException("前の探索が未完了またはメッシュ未準備");
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            try
            {
                var search = Svc.PluginInterface.GetIpcSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>>("vnavmesh.Nav.PathfindCancelable")
                    .InvokeFunc(from, to, flying, cancellation.Token);
                Interlocked.Increment(ref searches);
                _ = search.ContinueWith(t => { _ = t.Exception; Interlocked.Decrement(ref searches); cancellation.Dispose(); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return search;
            }
            catch { cancellation.Dispose(); throw; }
        }, cancel).ConfigureAwait(false);
        // The continuation above observes a late fault and retains the CTS until the real search ends.
        return new List<Vector3>(await task.WaitAsync(cancel).ConfigureAwait(false));
    }
    /// <summary>
    /// 曲線の部品（SmoothNav の MeshPointGround）が使う点の照会。曲線は裏のスレッドで作るので、IPC を直接呼ぶ
    /// （SmoothNav の Lab と同じ呼び方。点の照会は地図を読むだけで、経路探索の作業領域を使わない）。
    /// 呼べないときは「面が無い」＝その線は使わない。
    /// </summary>
    public Vector3? NearestPoint(Vector3 point, float halfExtentXZ, float halfExtentY)
    {
        try { return disposed || !ready() ? null : nearest(point, halfExtentXZ, halfExtentY); }
        catch (Exception) { return null; }
    }
    public Task<Vector3?> ProjectAsync(Vector3 point, bool flying, CancellationToken cancel)
        => Enqueue(() => !ready() ? null : flying ? (Vector3?)point : nearest(point, .5f, 1), cancel);
    public void Pump()
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed.TotalMilliseconds < 1 && queue.TryDequeue(out var job)) job();
        LastPumpMilliseconds = timer.Elapsed.TotalMilliseconds;
    }
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            while (queue.TryDequeue(out var job)) job();
        }
    }
}
