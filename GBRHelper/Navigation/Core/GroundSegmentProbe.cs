using System;
using System.Diagnostics;
using System.Numerics;

namespace GBRHelper.Navigation;

/// <summary>Height-continuous samples. Cursors retain progress, never native pointers, between slices.</summary>
public sealed class GroundSegmentProbe(Func<Vector3, float, float, Vector3?> nearest,
    Func<Vector3, Vector3, ProbeResult> body, Func<double>? clock = null)
{
    private readonly Func<Vector3, float, float, Vector3?> query = nearest;
    private readonly Func<Vector3, Vector3, ProbeResult> inspectBody = body;
    private readonly Func<double> now = clock ?? (() => Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency);
    public Cursor Begin(Vector3 from, Vector3 to) => new(this, from, to);
    public ProbeResult Check(Vector3 from, Vector3 to, double milliseconds = 1) => Begin(from, to).Advance(milliseconds, 4096);

    public sealed class Cursor
    {
        private readonly GroundSegmentProbe probe;
        private readonly Vector3 from, to;
        private readonly int count;
        private Vector3 previous;
        private int sample, slices;
        private double nearestMs, bodyMs, elapsedMs;
        private bool initialized;
        private ProbeResult? terminal;
        public bool Complete => terminal != null;
        public Cursor(GroundSegmentProbe probe, Vector3 from, Vector3 to)
        {
            this.probe = probe; this.from = from; this.to = to;
            if (!Geometry.Finite(from) || !Geometry.Finite(to))
                terminal = ProbeResult.Unknown("非有限座標", ProbeCode.InvalidInput);
            else if (Geometry.Horizontal(from, to) > 1024)
                terminal = ProbeResult.Unknown("地上照会範囲外", ProbeCode.Limit);
            else count = Math.Max(1, (int)Math.Ceiling(Geometry.Horizontal(from, to) / .25f));
        }
        public ProbeResult Advance(double milliseconds = 1, int maxSamples = 32)
        {
            var start = probe.now(); slices++;
            ProbeResult Finish(ProbeResult result, bool complete)
            {
                elapsedMs += Math.Max(0, probe.now() - start);
                result = result with { Metrics = new(sample, slices, nearestMs, bodyMs, elapsedMs) };
                if (complete) terminal = result;
                return result;
            }
            var timeout = ProbeResult.Unknown("地上照会の時間上限", ProbeCode.TimeBudget);
            if (terminal != null) return terminal;
            if (milliseconds <= 0 || maxSamples <= 0) return Finish(timeout, false);
            if (!initialized)
            {
                var t = probe.now(); var first = probe.query(from, .05f, 1); nearestMs += Math.Max(0, probe.now() - t);
                if (!Valid(first, from)) return Finish(new(Verdict.Blocked, "始点の面を確認できません", Code: ProbeCode.MeshMismatch), true);
                previous = first!.Value; initialized = true;
            }
            var completedThisSlice = 0;
            while (sample < count)
            {
                if (probe.now() - start >= milliseconds || completedThisSlice >= maxSamples) return Finish(timeout, false);
                var p = Vector3.Lerp(from, to, (sample + 1) / (float)count); p.Y = previous.Y;
                var t = probe.now(); var next = probe.query(p, .05f, 1); nearestMs += Math.Max(0, probe.now() - t);
                if (!Valid(next, p)) return Finish(new(Verdict.Blocked, "面の途切れ・段差・別階", EvidenceKind.Mesh, p, Code: ProbeCode.MeshMismatch), true);
                t = probe.now(); var result = probe.inspectBody(previous, next!.Value); bodyMs += Math.Max(0, probe.now() - t);
                if (result.Verdict != Verdict.Clear) return Finish(result, true);
                previous = next.Value; sample++; completedThisSlice++;
            }
            return Finish(Math.Abs(previous.Y - to.Y) <= 1 ? ProbeResult.Clear
                : new(Verdict.Blocked, "終点が別の高さの面", EvidenceKind.Mesh, to, Code: ProbeCode.MeshMismatch), true);
        }
        private static bool Valid(Vector3? p, Vector3 reference) => p.HasValue && Geometry.Finite(p.Value)
            && Geometry.Horizontal(p.Value, reference) <= .05f && Math.Abs(p.Value.Y - reference.Y) <= 1;
    }
}

