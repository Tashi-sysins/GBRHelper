using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace GBRHelper.Navigation;

/// <summary>Pure route processing. It never issues movement, stop, or Auto-Gather commands.</summary>
public sealed class RouteProcessor(IRouteQueries queries)
{
    public async Task<RouteResult> ProcessAsync(RouteRequest request, IReadOnlyList<Vector3> source, CancellationToken cancel)
    {
        // 避ける場所（飛行中に止まった所・先の区間で衝突が見つかった所）があれば、すべての線の確かめに重ねる。
        var q = request.Avoid is { Count: > 0 } zones ? new AvoidZoneQueries(queries, zones) : queries;
        var original = source.ToList();
        var c = request.Settings;
        // 騎乗開始中の経路も加工する（前の版は素通しで、実機では飛行の要求の 6 割がこれだった）。
        // GBR は乗り終えたら今の位置から水平 4m 以内の点を飛ばして飛行へ切り替えるので、出発から MountKeepDistance までは公式のまま残す。
        if (source.Count <= 1 || source.Count > c.MaxPoints)
            return new(RouteDisposition.Original, original, "一点・過大経路は従来経路");
        var decoded = GbrPathCodec.Decode(source, request.Flying, request.Combined);
        if (decoded == null) return new(RouteDisposition.Original, original, "経路または着地境界を解読できません");
        if (decoded.Parts.Any(p => p.Flying ? !c.Flight : !c.Ground))
            return new(RouteDisposition.Original, original, "この移動形態の加工は無効です");

        // All new lines, including retained portions of a detour, require Clear.
        var searches = 0; var detoured = false; var changed = false;
        var clearCount = 0; var unknownCount = 0; var blocked = false;
        ProbeResult? obstructionProbe = null;
        try
        {
            using var total = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            total.CancelAfter(c.RerouteMilliseconds + c.RefineMilliseconds);
            foreach (var part in decoded.Parts)
            {
                if (part.Points.Count < 2) continue;
                var checks = await q.CheckAsync(Geometry.Lines(part.Points, part.Flying), total.Token, probe =>
                {
                    request.Evidence.MarkBlocked();
                    Interlocked.CompareExchange(ref obstructionProbe, probe, null);
                }).ConfigureAwait(false);
                clearCount += checks.Count(x => x.Verdict == Verdict.Clear);
                unknownCount += checks.Count(x => x.Verdict == Verdict.Unknown);
                var obstruction = Array.FindIndex(checks, x => x.ReliableBlock);
                if (obstruction >= 0)
                {
                    request.Evidence.MarkBlocked();
                    blocked = true;
                    obstructionProbe = checks[obstruction];
                    if (!c.Detours) return new(RouteDisposition.NoSafeCandidate, original, "衝突を検出しましたが迂回は無効です", searches, true, Probe: obstructionProbe, Code: "DetoursDisabled");
                    var rerouted = await RerouteAsync(q, part.Points, part.Flying, checks, obstruction,
                        c, c.MaxSearches - searches, total.Token).ConfigureAwait(false);
                    searches += rerouted.Searches;
                    if (rerouted.Path == null)
                        return new(RouteDisposition.NoSafeCandidate, original, "予算内に検査済みの迂回経路を得られませんでした", searches, true, Probe: obstructionProbe, Code: "NoSafeCandidate");
                    part.Points.Clear(); part.Points.AddRange(rerouted.Path);
                    detoured = changed = true;
                }
            }
            using var refinement = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
            refinement.CancelAfter(c.RefineMilliseconds);
            // If smoothing expires, retain the already verified detour, never the blocked original.
            foreach (var part in decoded.Parts)
            {
                try
                {
                    var keep = request.Mounting && ReferenceEquals(part, decoded.Parts[0]) ? MountKeepCount(part.Points, c.MountKeepDistance) : 0;
                    var curve = await RoundCornersAsync(q, part.Points, part.Flying, c, refinement.Token, keep).ConfigureAwait(false);
                    if (!curve.SequenceEqual(part.Points))
                    {
                        part.Points.Clear(); part.Points.AddRange(curve); changed = true;
                    }
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { break; }
            }
            cancel.ThrowIfCancellationRequested();
            var encoded = decoded.Encode();
            if (encoded.Count > c.MaxPoints || encoded.Any(p => !Geometry.Finite(p)))
                return new(blocked ? RouteDisposition.NoSafeCandidate : RouteDisposition.Original, original, "点数・座標の上限", searches, blocked, Probe: obstructionProbe, Code: "InvalidResult");
            return new(detoured ? RouteDisposition.Detour : changed ? RouteDisposition.Refined : RouteDisposition.Original,
                changed ? encoded : original, detoured ? "公式探索の中継経路を全線検査" : changed ? "変更線Clear・最終接近保持" : "新線を採用せず従来経路を保持",
                searches, blocked, clearCount, unknownCount);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            blocked |= request.Evidence.ReliableBlock;
            return new(blocked ? RouteDisposition.NoSafeCandidate : RouteDisposition.Original, original, "処理期限を超過", searches, blocked, Probe: obstructionProbe, Code: "TimeBudget");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            blocked |= request.Evidence.ReliableBlock;
            return new(blocked ? RouteDisposition.NoSafeCandidate : RouteDisposition.Original, original, "加工例外: " + ex.GetType().Name + ": " + ex.Message, searches, blocked, Probe: obstructionProbe, Code: "ProcessorException");
        }
    }

    // 飛行の迂回で中継点を置く向き（横・上・下の組み合わせ。m）。近い横・上から順に試す。
    private static readonly (float Side, float Up)[] FlightOffsets =
        [(6, 0), (-6, 0), (0, 8), (12, 0), (-12, 0), (0, 16), (6, 8), (-6, 8), (20, 0), (-20, 0), (0, -8)];

    /// <summary>
    /// 衝突した線（index 番目の点から次の点へ）を避ける経路を探す。
    /// 地上：公式の直接の探索 → 衝突点の左右 4m を中継する公式の探索（前の版のまま）。
    /// 飛行：衝突点の手前の横・上・下に中継点を置き、元の経路の先の点（1〜4 個先）へ直接つなぐ（探索を使わない）→
    /// 公式の直接の探索 → 中継点から先を公式に探す。
    /// 候補は <see cref="Acceptable"/>：どの線も「通れない」でなく、衝突した所の近くの線は「通れる」と確かめられたもの。
    /// </summary>
    private static async Task<(List<Vector3>? Path, int Searches)> RerouteAsync(IRouteQueries q, List<Vector3> input, bool fly,
        ProbeResult[] checks, int index, RouteSettings c, int remaining, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(c.RerouteMilliseconds);
        var token = timeout.Token;
        var count = 0;
        var seen = new HashSet<string>();
        var hit = checks[index].Point ?? (input[index] + input[index + 1]) * .5f;
        bool Fits(List<Vector3> path)
        {
            if (path.Count < 2 || path.Count > c.MaxPoints || path.Any(p => !Geometry.Finite(p))
                || Vector3.Distance(path[0], input[0]) > .1f || Vector3.Distance(path[^1], input[^1]) > .1f) return false;
            // Preserve exact endpoints; any correction creates a new line and is checked below.
            path[0] = input[0]; path[^1] = input[^1];
            var signature = string.Join(";", path.Select(p => $"{MathF.Round(p.X * 4)},{MathF.Round(p.Y * 4)},{MathF.Round(p.Z * 4)}"));
            return seen.Add(signature) && Geometry.Length(path) <= Geometry.Length(input) * c.DetourLengthRatio + c.DetourExtraDistance;
        }
        // 衝突の近くを通る線が「確認できない」とき、近くの部分（衝突点から DetourNearDistance 以内）だけを確かめ直す。
        // 長い公式の線は遠い端が読み込まれていないだけで線全体が「確認できない」になるため（遠くは近づいてから見直す）。
        async Task<bool> Accept(IReadOnlyList<Segment> lines, ProbeResult[] verdicts)
        {
            for (var i = 0; i < lines.Count && i < verdicts.Length; i++)
            {
                if (verdicts[i].Verdict != Verdict.Unknown || NearPart(lines[i], hit, c.DetourNearDistance) is not { } near) continue;
                var again = await q.CheckAsync([near], token).ConfigureAwait(false);
                if (again.Length == 1 && again[0].Verdict == Verdict.Clear) verdicts[i] = ProbeResult.Clear;
                else if (again.Length == 1 && again[0].Verdict == Verdict.Blocked) verdicts[i] = again[0];
            }
            return Acceptable(lines, verdicts, hit, c);
        }
        async Task<List<Vector3>?> Check(List<Vector3> path)
        {
            if (!Fits(path)) return null;
            var lines = Geometry.Lines(path, fly);
            var verdicts = await q.CheckAsync(lines, token).ConfigureAwait(false);
            return await Accept(lines, verdicts).ConfigureAwait(false) ? path : null;
        }
        var direction = input[index + 1] - input[index];
        var flat = direction with { Y = 0 };
        if (flat.LengthSquared() < .001f) flat = Vector3.UnitZ;
        var side = Vector3.Normalize(Vector3.Cross(Vector3.Normalize(flat), Vector3.UnitY));
        // 飛行の中継点が歩ける床の近く（FlightFloorClearance 以内）なら使わない（着地してしまい、飛び直しでジャンプを繰り返す）。
        bool TooLow(Vector3 middle) => q is SmoothNav.Core.IMeshPointQuery mesh && mesh.NearestPoint(middle, .5f, c.FlightFloorClearance) != null;
        // 中継点は衝突点の 2m 手前を基準に置く（壁の面そのものに中継点を置かない）。
        var basePoint = hit - (direction.LengthSquared() > .001f ? Vector3.Normalize(direction) * 2 : Vector3.Zero);

        if (fly)
        {
            // 元の経路の線の答えは使い回し、新しい 2 本（衝突の手前の点→中継点→先の点）だけ確かめる。
            foreach (var (s, u) in FlightOffsets)
            {
                var middle = basePoint + side * s + Vector3.UnitY * u;
                if (TooLow(middle)) continue;
                for (var k = index + 1; k <= Math.Min(index + 4, input.Count - 1); k++)
                {
                    token.ThrowIfCancellationRequested();
                    var path = input.Take(index + 1).Append(middle).Concat(input.Skip(k)).ToList();
                    if (!Fits(path)) continue;
                    var added = await q.CheckAsync([new Segment(input[index], middle, true), new Segment(middle, input[k], true)], token).ConfigureAwait(false);
                    var verdicts = checks.Take(index).Concat(added).Concat(checks.Skip(k)).ToArray();
                    if (await Accept(Geometry.Lines(path, true), verdicts).ConfigureAwait(false)) return (path, count);
                }
            }
        }
        if (remaining <= 0) return (null, count);
        count++;
        var direct = await q.FindAsync(input[0], input[^1], fly, token).ConfigureAwait(false);
        if (await Check(direct).ConfigureAwait(false) is { } clean) return (clean, count);
        if (fly)
        {
            // 中継点までは元の経路と直線、中継点から先は公式の探索（1 候補 1 回の探索）。
            foreach (var (s, u) in FlightOffsets)
            {
                if (count + 1 > remaining) break;
                var middle = basePoint + side * s + Vector3.UnitY * u;
                if (TooLow(middle)) continue;
                count++;
                var tail = await q.FindAsync(middle, input[^1], true, token).ConfigureAwait(false);
                if (tail.Count == 0 || Vector3.Distance(tail[0], middle) > .1f) continue;
                var path = input.Take(index + 1).Concat(tail).ToList();
                if (await Check(path).ConfigureAwait(false) is { } detour) return (detour, count);
            }
            return (null, count);
        }
        // Two candidate sides; both halves must come from the existing cancelable official search.
        foreach (var sign in new[] { 1f, -1f })
        {
            if (count + 2 > remaining) break;
            var middle = await q.ProjectAsync(hit + side * (4 * sign), fly, token).ConfigureAwait(false);
            if (middle == null) continue;
            count++;
            var a = await q.FindAsync(input[0], middle.Value, fly, token).ConfigureAwait(false);
            if (a.Count == 0 || Vector3.Distance(a[^1], middle.Value) > .1f) continue;
            count++;
            var b = await q.FindAsync(middle.Value, input[^1], fly, token).ConfigureAwait(false);
            if (b.Count == 0 || Vector3.Distance(b[0], middle.Value) > .1f) continue;
            // Check the connecting line too; never simply drop a point across a gap.
            a.AddRange(b);
            if (await Check(a).ConfigureAwait(false) is { } detour) return (detour, count);
        }
        return (null, count);
    }

    /// <summary>
    /// 迂回の候補を使ってよいか。どの線も「通れない」でなく、衝突した所から DetourNearDistance 以内を通る線は「通れる」と確かめられたもの。
    /// それより遠い線が「確認できない」（当たり判定がまだ読み込まれていない）のは許す：飛んでいる間に近づいてから確かめ直す
    /// （<see cref="FlightLookahead"/>）。前の版はすべて「通れる」を求めたため、遠くの線が必ず「確認できない」になる
    /// 長い飛行では、迂回を 1 つも採用できなかった。
    /// </summary>
    /// <summary>線のうち、点から radius 以内にある部分（無ければ null。線全体なら確かめ直しても同じなので null）。</summary>
    public static Segment? NearPart(Segment s, Vector3 point, float radius)
    {
        var d = s.To - s.From; var a = d.LengthSquared();
        if (a < 1e-9f) return null;
        var f = s.From - point; var b = 2 * Vector3.Dot(f, d); var e = Vector3.Dot(f, f) - radius * radius;
        var discriminant = b * b - 4 * a * e;
        if (discriminant < 0) return null;
        var root = MathF.Sqrt(discriminant);
        var t1 = Math.Clamp((-b - root) / (2 * a), 0, 1); var t2 = Math.Clamp((-b + root) / (2 * a), 0, 1);
        if (t2 - t1 < 1e-4f || (t1 <= 0 && t2 >= 1)) return null;
        return new(s.From + d * t1, s.From + d * t2, s.Flying);
    }
    public static bool Acceptable(IReadOnlyList<Segment> lines, IReadOnlyList<ProbeResult> verdicts, Vector3 hit, RouteSettings c)
    {
        if (verdicts.Count != lines.Count) return false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (verdicts[i].Verdict == Verdict.Blocked) return false;
            if (verdicts[i].Verdict != Verdict.Clear && AvoidZones.Distance(hit, lines[i].From, lines[i].To) <= c.DetourNearDistance) return false;
        }
        return true;
    }

    /// <summary>
    /// 角を曲線にする。曲線は SmoothNav の CurveBuilder（<see cref="SmoothNavCurve"/>）で作る。
    /// 最後の区間と終点（着地の目印）は GBR の経路のまま残す。keepStart 個目までの点も残す（騎乗の切り替え用）。
    /// CurveBuilder は同期で照会するので、フレームのスレッドを塞がないよう裏のスレッドで動かす。
    /// 打ち切りの合図で残りの角は公式のまま返る（例外にしない）。
    /// </summary>
    public Task<List<Vector3>> RoundCornersAsync(IReadOnlyList<Vector3> input, bool fly, RouteSettings c, CancellationToken cancel, int keepStart = 0)
        => RoundCornersAsync(queries, input, fly, c, cancel, keepStart);
    private static Task<List<Vector3>> RoundCornersAsync(IRouteQueries q, IReadOnlyList<Vector3> input, bool fly, RouteSettings c, CancellationToken cancel, int keepStart)
    {
        var copy = input.ToList();
        if (copy.Count < 4) return Task.FromResult(copy);
        return Task.Run(() => SmoothNavCurve.Round(copy, fly, keepStart, q, c, cancel), CancellationToken.None);
    }

    /// <summary>出発点から水平に distance 以上離れた最初の点の位置（その手前までは公式のまま残す）。</summary>
    public static int MountKeepCount(IReadOnlyList<Vector3> points, float distance)
    {
        for (var i = 1; i < points.Count; i++)
            if (Geometry.Horizontal(points[0], points[i]) >= distance) return i;
        return points.Count;
    }
}
