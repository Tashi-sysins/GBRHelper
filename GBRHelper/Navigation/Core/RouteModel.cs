using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace GBRHelper.Navigation;

public enum NavigationMode { Off, Observe, Improve }
public enum Verdict { Clear, Blocked, Unknown }
// Stall：その場所で実際に進めなくなった（ゲームの中の停滞）。当たり判定の線が通っても、同じ向きに押し込む線は使わない。
public enum EvidenceKind { Mesh, Collision, Unavailable, Stall }
public enum ProbeCode { None, TimeBudget, NotReady, Unloaded, MeshMismatch, InvalidInput, Limit, Collision, AmbiguousSurface, QueryException, Cancelled, Stall }
public sealed record ProbeMetrics(int Samples, int Slices, double NearestMilliseconds, double BodyMilliseconds, double ElapsedMilliseconds);
public sealed record ProbeResult(Verdict Verdict, string Reason = "", EvidenceKind Evidence = EvidenceKind.Mesh,
    Vector3? Point = null, Vector3? Normal = null, ProbeCode Code = ProbeCode.None, ProbeMetrics? Metrics = null)
{
    public bool ReliableBlock => Verdict == Verdict.Blocked && Evidence is EvidenceKind.Collision or EvidenceKind.Stall;
    public static readonly ProbeResult Clear = new(Verdict.Clear);
    public static ProbeResult Unknown(string why, ProbeCode code = ProbeCode.NotReady) => new(Verdict.Unknown, why, EvidenceKind.Unavailable, Code: code);
}
public sealed record Segment(Vector3 From, Vector3 To, bool Flying);
/// <summary>
/// 避ける場所：飛行中に進めなくなった所・先の区間の当たり判定で衝突が見つかった所。
/// Point から Radius 以内を、Direction と同じ向き（60 度以内）へ通る線は使わない（別の向きへ離れる線は使ってよい）。
/// </summary>
public sealed record AvoidZone(Vector3 Point, Vector3 Direction, float Radius, string Reason);
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
// 飛行中の見直し（10-12 嘆きの海で壁へ飛び続けた件）：
//   LookaheadDistance：飛んでいる間、今の位置から先のこの距離までの区間を当たり判定で確かめ直す
//     （出発の時は遠くの当たり判定がまだ読み込まれておらず「確認できない」になるため）。
//   DetourNearDistance：迂回の候補で、衝突した所からこの距離以内を通る線は「通れる」と確かめられたものだけ使う
//     （それより遠い公式の線は、近づいてから見直す）。
//   AvoidRadius：避ける場所の半径。MaxFlightReplans：1 つの採取先で飛行中に経路を求め直す回数の上限
//     （使い切ったら GBR 自身の詰まり対策に任せる。自動採集は止めない）。
//   ReachedRadius：空中で経路を渡すとき、自分からこの距離以内の先頭の点は外す（10-12 パーラカの里：
//     求め直した瞬間の位置が先頭の点になり、上昇の勢いで少し離れたその点の周りを回り続けた。vnavmesh は 0.25m 以内に
//     入らないと点を通ったことにしないので、飛ぶ速さでは合わせきれない）。GBR も乗り終えたときに近くの点を飛ばしている。
//   OrbitRadius：次の点がこの距離以内にあるのに、GBR のしきい値の 3/4 の間その点を通れなければ「回り続けている」として求め直す。
//   LookaheadIgnoreNear：先読みで見つけた衝突が自分からこの距離以内なら求め直さない（体が触れている所。飛び立ちの直後に
//     エーテライト・柱の横で出る。求め直しても避けられず、飛び立ちを邪魔するだけだった＝10-12 オックハヌ・パーラカの里）。
//   FlightFloorClearance：飛行の迂回の中継点は、歩ける床からこの高さ以内に置かない（床の高さへ下りると着地してしまい、
//     次の点へ飛び直すのにジャンプを繰り返した＝10-12 オックハヌ）。
public sealed record RouteSettings(int RefineMilliseconds = 500, int RerouteMilliseconds = 2500,
    int MaxSearches = 5, int MaxPoints = 2048, float CurveSpacing = .5f, float MountKeepDistance = 8,
    float CurveLengthRatio = 1.05f, float DetourLengthRatio = 4, float DetourExtraDistance = 100,
    bool Ground = true, bool Flight = false, bool Detours = true,
    float LookaheadDistance = 60, float DetourNearDistance = 20, float AvoidRadius = 3, int MaxFlightReplans = 4,
    float ReachedRadius = 3, float OrbitRadius = 10, float LookaheadIgnoreNear = 3, float FlightFloorClearance = 2.5f);
public sealed record RouteRequest(Guid Session, long Attempt, long Id, long Generation, int Revision,
    uint Territory, uint ItemId, string Node, Vector3 Start, Vector3 Destination, Vector3 CorrectedDestination,
    bool Flying, bool Combined, bool Mounting, RouteSettings Settings, IReadOnlyList<AvoidZone>? Avoid = null)
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

    /// <summary>
    /// 空中で渡す経路の先頭から、自分から radius 以内の点を外す（GBR が受け取る点の並びをそのまま直す）。
    /// 着地の目印（combined の最後の点）と、飛行の部分の最後の 2 点は外さない。外した数を返す。
    /// </summary>
    public static int TrimReached(List<Vector3> path, Vector3 position, bool combined, float radius)
    {
        if (path.Count < 2 || !Geometry.Finite(position)) return 0;
        var flightEnd = path.Count - 1;
        if (combined)
        {
            var marker = path[^1];
            flightEnd = path.FindIndex(0, path.Count - 1, p => p == marker);
            if (flightEnd < 0) return 0;
        }
        var n = 0;
        while (n < flightEnd - 1 && Vector3.Distance(path[n], position) < radius) n++;
        if (n > 0) path.RemoveRange(0, n);
        return n;
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
