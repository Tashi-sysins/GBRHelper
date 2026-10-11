using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace GBRHelper.Navigation;

/// <summary>The original task always completes before the proxy: GBR owns the original CTS lifetime.</summary>
public sealed class RouteTicket
{
    private readonly CancellationTokenSource work = new();
    private int valid = 1;
    private RouteResult? result;
    private Func<RouteRequest, IReadOnlyList<Vector3>, CancellationToken, Task<RouteResult>>? processing;
    private long processingStarted;
    public Vector3[] OriginalPath { get; private set; } = [];
    public RouteRequest Request { get; }
    public Task<List<Vector3>> Original { get; }
    public Task<List<Vector3>> Proxy { get; }
    public CancellationTokenSource OriginalCancellation { get; }
    public RouteResult? Result => Volatile.Read(ref result);
    public bool Valid => Volatile.Read(ref valid) != 0;
    public long ProcessingStarted => Interlocked.Read(ref processingStarted);
    public bool HoldingRecovery => Valid && ProcessingStarted > 0 && !Proxy.IsCompleted
        && Environment.TickCount64 - ProcessingStarted <= Request.Settings.RerouteMilliseconds + Request.Settings.RefineMilliseconds;

    public RouteTicket(RouteRequest request, Task<List<Vector3>> original, CancellationTokenSource cts,
        Func<RouteRequest, IReadOnlyList<Vector3>, CancellationToken, Task<RouteResult>> process, bool observe)
    {
        Request = request; Original = original; OriginalCancellation = cts;
        processing = process;
        Proxy = Run(observe);
        // Also observe abandoned faults; never turn original failure into success.
        _ = Proxy.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private async Task<List<Vector3>> Run(bool observe)
    {
        try
        {
            var raw = await Original.ConfigureAwait(false); // no helper timeout on the original
            var saved = new List<Vector3>(raw);
            OriginalPath = saved.ToArray();
            var process = Volatile.Read(ref processing);
            if (!Valid || process == null) return saved;
            if (observe) { Volatile.Write(ref result, new(RouteDisposition.Original, saved, "観測のみ：元点列の写しを返却")); return saved; }
            Interlocked.Exchange(ref processingStarted, Environment.TickCount64);
            try
            {
                var task = process(Request, saved, work.Token);
                _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                var response = await task.WaitAsync(TimeSpan.FromMilliseconds(Request.Settings.RerouteMilliseconds + Request.Settings.RefineMilliseconds + 100), work.Token).ConfigureAwait(false);
                if (!Valid) return saved;
                Volatile.Write(ref result, response);
                return new List<Vector3>(response.Path);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Helper OFF / budget / helper fault do not become a faulted or empty GBR task.
                Volatile.Write(ref result, new(Request.Evidence.ReliableBlock ? RouteDisposition.NoSafeCandidate : RouteDisposition.Original,
                    saved, "加工中止: " + ex.GetType().Name, OriginalBlocked: Request.Evidence.ReliableBlock,
                    Code: ex is TimeoutException or OperationCanceledException ? "TimeBudget" : "ProcessorException"));
                return saved;
            }
        }
        finally { Interlocked.Exchange(ref processing, null); work.Cancel(); } // expire abandoned query jobs without disposing a CTS another thread can still use
    }
    public void Invalidate() { Interlocked.Exchange(ref valid, 0); Interlocked.Exchange(ref processing, null); work.Cancel(); }
}
