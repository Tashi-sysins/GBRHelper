using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace GBRHelper.Navigation;

/// <summary>Proves only that GBR's ORIGINAL landing operation is unchanged; never claims collision Clear.</summary>
public sealed class OriginalLandingHandoff
{
    private readonly Vector3[] ground;
    private readonly Vector3 approach;
    private OriginalLandingHandoff(Vector3[] ground, Vector3 approach) { this.ground = ground; this.approach = approach; }
    public static OriginalLandingHandoff? Create(RouteRequest request, IReadOnlyList<Vector3> original, RouteResult result)
    {
        // 騎乗開始中の経路も曲線にするので除かない（着地の線と地上の残りは曲線でも変えない）。
        if (!request.Combined || !request.Flying || result.OriginalBlocked || request.Evidence.ReliableBlock)
            return null;
        var a = GbrPathCodec.Decode(original, true, true); var b = GbrPathCodec.Decode(result.Path, true, true);
        if (a == null || b == null || a.Parts.Count != 2 || b.Parts.Count != 2 || a.Landing != b.Landing
            || a.Parts[0].Points.Count < 2 || b.Parts[0].Points.Count < 2 || a.Parts[1].Points.Count < 2
            || !a.Parts[1].Points.SequenceEqual(b.Parts[1].Points)
            || !a.Parts[0].Points.TakeLast(2).SequenceEqual(b.Parts[0].Points.TakeLast(2))) return null;
        return new(a.Parts[1].Points.ToArray(), a.Parts[0].Points[^2]);
    }
    public bool Matches(IReadOnlyList<Vector3> remaining, Vector3 position, int landCount, float tolerance)
    {
        // Tolerance is read from the current follower; never widen it or reuse a cached Clear.
        if (!Geometry.Finite(position) || !float.IsFinite(tolerance) || tolerance < 0 || tolerance > .5f
            || landCount != ground.Length || remaining.Count == 0 || remaining.Count >= ground.Length
            || !remaining.SequenceEqual(ground.Skip(ground.Length - remaining.Count))) return false;
        var index = ground.Length - remaining.Count;
        var previous = ground[index - 1];
        if (Distance(position, previous, ground[index]) <= tolerance) return true;
        // A waypoint can be consumed while the body still descends on the retained incoming flight segment.
        return index == 1 && Distance(position, approach, ground[0]) <= tolerance;
    }
    private static float Distance(Vector3 p, Vector3 a, Vector3 b)
    {
        var d = b - a; var length = d.LengthSquared();
        return Vector3.Distance(p, length == 0 ? a : a + d * Math.Clamp(Vector3.Dot(p - a, d) / length, 0, 1));
    }
}
