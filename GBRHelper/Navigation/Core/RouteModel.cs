using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace GBRHelper.Navigation;

public enum NavigationMode { Off, Observe, Improve }
public enum Verdict { Clear, Blocked, Unknown }
public enum EvidenceKind { Mesh, Collision, Unavailable }
public enum ProbeCode { None, TimeBudget, NotReady, Unloaded, MeshMismatch, InvalidInput, Limit, Collision, AmbiguousSurface, QueryException, Cancelled }
public sealed record ProbeMetrics(int Samples, int Slices, double NearestMilliseconds, double BodyMilliseconds, double ElapsedMilliseconds);
public sealed record ProbeResult(Verdict Verdict, string Reason = "", EvidenceKind Evidence = EvidenceKind.Mesh,
    Vector3? Point = null, Vector3? Normal = null, ProbeCode Code = ProbeCode.None, ProbeMetrics? Metrics = null)
{
    public bool ReliableBlock => Verdict == Verdict.Blocked && Evidence == EvidenceKind.Collision;
    public static readonly ProbeResult Clear = new(Verdict.Clear);
    public static ProbeResult Unknown(string why, ProbeCode code = ProbeCode.NotReady) => new(Verdict.Unknown, why, EvidenceKind.Unavailable, Code: code);
}
public sealed record Segment(Vector3 From, Vector3 To, bool Flying);
public interface IRouteQueries
{
    Task<ProbeResult[]> CheckAsync(IReadOnlyList<Segment> segments, CancellationToken cancel, Action<ProbeResult>? onReliableBlock = null);
    Task<List<Vector3>> FindAsync(Vector3 from, Vector3 to, bool flying, CancellationToken cancel);
    Task<Vector3?> ProjectAsync(Vector3 point, bool flying, CancellationToken cancel);
}

// Development budgets, not measured guarantees. No late route replacement or endpoint trimming.
// RefineMilliseconds：曲線（SmoothNav の CurveBuilder）の予算。飛行の照会はフレームの待ち行列を通るので 1 回の確かめに 1 フレームかかる。
// 予算を超えた角は公式のまま（打ち切り）。MountKeepDistance：騎乗開始中の経路で、出発から公式のまま残す水平の距離
// （GBR は乗り終えたら今の位置から水平 4m 以内の点を飛ばすので、それより長く残す）。
public sealed record RouteSettings(int RefineMilliseconds = 500, int RerouteMilliseconds = 2500,
    int MaxSearches = 5, int MaxPoints = 2048, float CurveSpacing = .5f, float MountKeepDistance = 8,
    float CurveLengthRatio = 1.05f, float DetourLengthRatio = 4, float DetourExtraDistance = 100,
    bool Ground = true, bool Flight = false, bool Detours = true);
public sealed record RouteRequest(Guid Session, long Attempt, long Id, long Generation, int Revision,
    uint Territory, uint ItemId, string Node, Vector3 Start, Vector3 Destination, Vector3 CorrectedDestination,
    bool Flying, bool Combined, bool Mounting, RouteSettings Settings)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public RouteEvidence Evidence { get; } = new();
}
public sealed class RouteEvidence
{
    private int blocked;
    public bool ReliableBlock => Volatile.Read(ref blocked) != 0;
    public void MarkBlocked() => Interlocked.Exchange(ref blocked, 1);
}
public enum RouteDisposition { Original, Refined, Detour, NoSafeCandidate }
public sealed record RouteResult(RouteDisposition Disposition, List<Vector3> Path, string Reason,
    int Searches = 0, bool OriginalBlocked = false, int ClearSegments = 0, int UnknownSegments = 0,
    ProbeResult? Probe = null, string Code = "");

public static class Geometry
{
    public static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    public static float Horizontal(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));
    public static float Length(IReadOnlyList<Vector3> p)
    {
        var sum = 0f;
        for (var i = 1; i < p.Count; i++) sum += Vector3.Distance(p[i - 1], p[i]);
        return sum;
    }
    public static List<Segment> Lines(IReadOnlyList<Vector3> p, bool fly)
        => Enumerable.Range(1, Math.Max(0, p.Count - 1)).Select(i => new Segment(p[i - 1], p[i], fly)).ToList();
}

public sealed record RoutePart(List<Vector3> Points, bool Flying);
public sealed record DecodedPath(List<RoutePart> Parts, Vector3? Landing)
{
    public List<Vector3> Encode()
    {
        var result = new List<Vector3>(Parts[0].Points);
        foreach (var part in Parts.Skip(1)) result.AddRange(part.Points.Skip(1));
        if (Landing is { } landing) result.Add(landing);
        return result;
    }
}

public static class GbrPathCodec
{
    public static DecodedPath? Decode(IReadOnlyList<Vector3> raw, bool flying, bool combined)
    {
        if (raw.Count == 0 || raw.Any(p => !Geometry.Finite(p))) return null;
        if (!combined) return new([new(raw.ToList(), flying)], null);
        if (!flying || raw.Count < 3) return null;
        var marker = raw[^1];
        var indices = Enumerable.Range(0, raw.Count - 1).Where(i => raw[i] == marker).ToArray();
        // Ambiguous or missing landing markers are never guessed.
        if (indices.Length != 1) return null;
        var n = indices[0];
        return new([new(raw.Take(n + 1).ToList(), true), new(raw.Skip(n).Take(raw.Count - n - 1).ToList(), false)], marker);
    }

    public static List<Vector3> ForDispatch(IReadOnlyList<Vector3> raw, bool combined, Vector3 player, bool isPathing)
    {
        var path = raw.ToList();
        if (combined && path.Count > 0) path.RemoveAt(path.Count - 1);
        if (!isPathing || path.Count == 0) return path;
        var fwd = path[^1] - player; fwd.Y = 0;
        if (fwd.LengthSquared() < 1) return path;
        fwd = Vector3.Normalize(fwd);
        var n = 0;
        while (n < path.Count)
        {
            var next = path[n] - player; next.Y = 0;
            if (Vector3.Dot(fwd, next) > 0) break;
            n++;
        }
        path.RemoveRange(0, n);
        return path;
    }
}
