using System;
using System.Collections.Generic;
using System.Numerics;

namespace GBRHelper.Navigation;

/// <summary>
/// 飛行中に進めなくなったかを見る。地上の <see cref="ProgressWatch"/> は水平の距離で見るが、飛行は上下にも動くので 3 次元の距離で見る。
/// しきい値は GBR の NavResetThreshold（GBR 自身の詰まり対策が動き出す秒数）の 3/4。GBR より先に気づき、
/// ランダムな向きへの押し出しではなく、止まった所を避けた経路を求め直させる。
/// 時間は「諦めるまでの上限」ではなく「進んでいない時間の長さ」を測るためだけに使う（待ってから次へ進むのではない）。
/// </summary>
public sealed class StallWatch
{
    private const float MinimumMovement = 2;
    private Vector3 anchor;
    private long since;
    private bool tracking;

    public bool Observe(Vector3 position, long now, double thresholdSeconds, bool eligible)
    {
        if (!eligible || !Geometry.Finite(position)) { tracking = false; return false; }
        if (!tracking || Vector3.Distance(position, anchor) >= MinimumMovement)
        { tracking = true; anchor = position; since = now; return false; }
        if (!double.IsFinite(thresholdSeconds) || thresholdSeconds <= 0 || now - since < Math.Max(1000, thresholdSeconds * 750)) return false;
        since = now; anchor = position;
        return true;
    }
    public void Reset() => tracking = false;
}

/// <summary>
/// 次の点の近く（radius 以内）にいるのに、その点を通れないまま時間が過ぎたかを見る（点の周りを回り続ける・行ったり来たりする）。
/// 停滞の見張り（<see cref="StallWatch"/>）は 2m 動けば進んだとみなすので、点の周りを飛び回っている間は気づけない（10-12 パーラカの里）。
/// 次の点が替わる（通った）か、点から離れたら測り直す。
/// </summary>
public sealed class OrbitWatch
{
    private Vector3 waypoint;
    private long since;
    private bool tracking;

    public bool Observe(Vector3 position, Vector3 next, long now, double thresholdSeconds, float radius, bool eligible)
    {
        if (!eligible || !Geometry.Finite(position) || Vector3.Distance(position, next) > radius) { tracking = false; return false; }
        if (!tracking || Vector3.Distance(next, waypoint) > .01f) { tracking = true; waypoint = next; since = now; return false; }
        if (!double.IsFinite(thresholdSeconds) || thresholdSeconds <= 0 || now - since < Math.Max(1000, thresholdSeconds * 750)) return false;
        since = now;
        return true;
    }
    public void Reset() => tracking = false;
}

public enum LookaheadVerdict { Clear, Pending, Blocked }
public sealed record LookaheadResult(LookaheadVerdict Verdict, Segment? Segment = null, ProbeResult? Probe = null, int Probed = 0);

/// <summary>
/// 飛んでいる間に、今の位置から先 LookaheadDistance までの区間を当たり判定で確かめ直す。
/// 出発の時は遠くの当たり判定が読み込まれていない（620m 先の壁は「確認できない」だった）ので、近づいてから確かめる。
/// 1 回に確かめる線の数に上限を置き（フレームを重くしない）、通れると分かった区間は覚えて確かめ直さない。
/// 確認できない線は覚えず、次の回にまた確かめる（読み込まれるのを待つ）。
/// </summary>
public sealed class FlightLookahead
{
    private readonly HashSet<(Vector3 From, Vector3 To)> clear = new();
    private readonly HashSet<Vector3> clearEnds = new();

    /// <param name="ignoreNear">衝突の点が自分からこの距離以内なら「求め直す衝突」にしない（体が触れている所。覚えずに次の線を見る）。</param>
    public LookaheadResult Step(Vector3 position, IReadOnlyList<Vector3> remaining, int flightCount, float distance,
        Func<Vector3, Vector3, ProbeResult> probe, int maxProbes = 4, float ignoreNear = 0)
    {
        var count = Math.Min(flightCount, remaining.Count);
        var from = position; var travelled = 0f; var probed = 0; var pending = false;
        for (var i = 0; i < count && travelled < distance; i++)
        {
            var to = remaining[i];
            var length = Vector3.Distance(from, to);
            // 今の位置から次の点への線は、前に通れると分かった線（前の位置から同じ点へ）の上を進んでいる間は確かめ直さない。
            var known = i == 0 ? clearEnds.Contains(to) : clear.Contains((from, to));
            if (!known && length > .05f)
            {
                if (probed >= maxProbes) return new(LookaheadVerdict.Pending, Probed: probed);
                var segment = new Segment(from, to, true);
                // 先の区間が見る距離より長いときは、見る距離までの部分だけ確かめる（遠くはまだ読み込まれていない）。
                var end = travelled + length > distance ? from + (to - from) * ((distance - travelled) / length) : to;
                var result = probe(from, end); probed++;
                var touching = result.ReliableBlock && result.Point is { } hit && Vector3.Distance(hit, position) < ignoreNear;
                if (result.ReliableBlock && !touching) return new(LookaheadVerdict.Blocked, segment, result, probed);
                if (result.Verdict == Verdict.Clear && end == to) { if (i == 0) clearEnds.Add(to); else clear.Add((from, to)); }
                else pending = true;
            }
            travelled += length; from = to;
        }
        return new(pending ? LookaheadVerdict.Pending : LookaheadVerdict.Clear, Probed: probed);
    }
    public void Reset() { clear.Clear(); clearEnds.Clear(); }
}
