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
                var checks = await queries.CheckAsync(Geometry.Lines(part.Points, part.Flying), total.Token, probe =>
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
                    var rerouted = await RerouteAsync(part.Points, part.Flying, checks[obstruction], obstruction,
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
                    var curve = await RoundCornersAsync(part.Points, part.Flying, c, refinement.Token, keep).ConfigureAwait(false);
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

    private async Task<(List<Vector3>? Path, int Searches)> RerouteAsync(List<Vector3> input, bool fly,
        ProbeResult obstruction, int index, RouteSettings c, int remaining, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(c.RerouteMilliseconds);
        var token = timeout.Token;
        var count = 0;
        var seen = new HashSet<string>();
        async Task<List<Vector3>?> Check(List<Vector3> path)
        {
            if (path.Count < 2 || path.Count > c.MaxPoints || path.Any(p => !Geometry.Finite(p))
                || Vector3.Distance(path[0], input[0]) > .1f || Vector3.Distance(path[^1], input[^1]) > .1f) return null;
            // Preserve exact endpoints; any correction creates a new line and is checked below.
            path[0] = input[0]; path[^1] = input[^1];
            var signature = string.Join(";", path.Select(p => $"{MathF.Round(p.X * 4)},{MathF.Round(p.Y * 4)},{MathF.Round(p.Z * 4)}"));
            if (!seen.Add(signature)) return null;
            if (Geometry.Length(path) > Geometry.Length(input) * c.DetourLengthRatio + c.DetourExtraDistance) return null;
            var verdicts = await queries.CheckAsync(Geometry.Lines(path, fly), token).ConfigureAwait(false);
            return verdicts.All(v => v.Verdict == Verdict.Clear) ? path : null;
        }
        if (remaining <= 0) return (null, count);
        count++;
        var direct = await queries.FindAsync(input[0], input[^1], fly, token).ConfigureAwait(false);
        if (await Check(direct).ConfigureAwait(false) is { } clean) return (clean, count);
        var hit = obstruction.Point ?? (input[index] + input[index + 1]) * .5f;
        var direction = input[index + 1] - input[index]; direction.Y = 0;
        if (direction.LengthSquared() < .001f) direction = Vector3.UnitZ;
        var side = Vector3.Normalize(Vector3.Cross(Vector3.Normalize(direction), Vector3.UnitY));
        // Two candidate sides; both halves must come from the existing cancelable official search.
        foreach (var sign in new[] { 1f, -1f })
        {
            if (count + 2 > remaining) break;
            var middle = await queries.ProjectAsync(hit + side * (4 * sign), fly, token).ConfigureAwait(false);
            if (middle == null) continue;
            count++;
            var a = await queries.FindAsync(input[0], middle.Value, fly, token).ConfigureAwait(false);
            if (a.Count == 0 || Vector3.Distance(a[^1], middle.Value) > .1f) continue;
            count++;
            var b = await queries.FindAsync(middle.Value, input[^1], fly, token).ConfigureAwait(false);
            if (b.Count == 0 || Vector3.Distance(b[0], middle.Value) > .1f) continue;
            // Check the connecting line too; never simply drop a point across a gap.
            a.AddRange(b);
            if (await Check(a).ConfigureAwait(false) is { } detour) return (detour, count);
        }
        return (null, count);
    }

    /// <summary>
    /// 角を曲線にする。曲線は SmoothNav の CurveBuilder（<see cref="SmoothNavCurve"/>）で作る。
    /// 最後の区間と終点（着地の目印）は GBR の経路のまま残す。keepStart 個目までの点も残す（騎乗の切り替え用）。
    /// CurveBuilder は同期で照会するので、フレームのスレッドを塞がないよう裏のスレッドで動かす。
    /// 打ち切りの合図で残りの角は公式のまま返る（例外にしない）。
    /// </summary>
    public Task<List<Vector3>> RoundCornersAsync(IReadOnlyList<Vector3> input, bool fly, RouteSettings c, CancellationToken cancel, int keepStart = 0)
    {
        var copy = input.ToList();
        if (copy.Count < 4) return Task.FromResult(copy);
        return Task.Run(() => SmoothNavCurve.Round(copy, fly, keepStart, queries, c, cancel), CancellationToken.None);
    }

    /// <summary>出発点から水平に distance 以上離れた最初の点の位置（その手前までは公式のまま残す）。</summary>
    public static int MountKeepCount(IReadOnlyList<Vector3> points, float distance)
    {
        for (var i = 1; i < points.Count; i++)
            if (Geometry.Horizontal(points[0], points[i]) >= distance) return i;
        return points.Count;
    }
}
