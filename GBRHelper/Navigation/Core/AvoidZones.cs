using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace GBRHelper.Navigation;

/// <summary>
/// 避ける場所（<see cref="AvoidZone"/>）で線を判定する。
/// 当たり判定の細い線（身体の中心から 0.5m）は通るのに、騎乗の身体が隙間を抜けられず止まることがある。
/// 実際に止まった所へ同じ向きで押し込む線を「通れない」として扱い、迂回を探させる。
/// </summary>
public static class AvoidZones
{
    // 同じ向きとみなす角度（cos 60 度）。これより外れた向きへ離れる線は使ってよい（止まった所から出て行く線）。
    private const float SameDirection = .5f;

    public static ProbeResult? Hit(Segment segment, IReadOnlyList<AvoidZone>? zones)
    {
        if (zones == null || zones.Count == 0) return null;
        var delta = segment.To - segment.From; var length = delta.Length();
        if (length < .001f) return null;
        var direction = delta / length;
        foreach (var zone in zones)
        {
            var heading = zone.Direction.LengthSquared() < 1e-6f ? Vector3.Zero : Vector3.Normalize(zone.Direction);
            if (heading == Vector3.Zero || Vector3.Dot(direction, heading) < SameDirection) continue;
            if (Distance(zone.Point, segment.From, segment.To) > zone.Radius) continue;
            return new(Verdict.Blocked, "進めなくなった場所へ同じ向きで入る線: " + zone.Reason, EvidenceKind.Stall, zone.Point, -heading, ProbeCode.Stall);
        }
        return null;
    }

    /// <summary>点から線分までの距離。</summary>
    public static float Distance(Vector3 point, Vector3 a, Vector3 b)
    {
        var ab = b - a; var lengthSquared = ab.LengthSquared();
        if (lengthSquared < 1e-9f) return Vector3.Distance(point, a);
        var t = Math.Clamp(Vector3.Dot(point - a, ab) / lengthSquared, 0, 1);
        return Vector3.Distance(point, a + ab * t);
    }
}

/// <summary>
/// 照会に避ける場所の判定を重ねる。当たり判定が「通れる」「分からない」と答えた線でも、避ける場所へ押し込む線は「通れない」にする。
/// 当たり判定が既に「通れない」と答えた線はそのまま。曲線の確かめも同じ照会を通るので、曲線も避ける場所へ入らない。
/// </summary>
public sealed class AvoidZoneQueries(IRouteQueries inner, IReadOnlyList<AvoidZone> zones) : IRouteQueries, SmoothNav.Core.IMeshPointQuery
{
    public async Task<ProbeResult[]> CheckAsync(IReadOnlyList<Segment> segments, CancellationToken cancel, Action<ProbeResult>? onReliableBlock = null)
    {
        var results = await inner.CheckAsync(segments, cancel, onReliableBlock).ConfigureAwait(false);
        for (var i = 0; i < results.Length && i < segments.Count; i++)
        {
            if (results[i].ReliableBlock || AvoidZones.Hit(segments[i], zones) is not { } hit) continue;
            results[i] = hit;
            onReliableBlock?.Invoke(hit);
        }
        return results;
    }
    public Task<List<Vector3>> FindAsync(Vector3 from, Vector3 to, bool flying, CancellationToken cancel) => inner.FindAsync(from, to, flying, cancel);
    public Task<Vector3?> ProjectAsync(Vector3 point, bool flying, CancellationToken cancel) => inner.ProjectAsync(point, flying, cancel);
    public Vector3? NearestPoint(Vector3 point, float halfExtentXZ, float halfExtentY)
        => inner is SmoothNav.Core.IMeshPointQuery mesh ? mesh.NearestPoint(point, halfExtentXZ, halfExtentY) : null;
}
