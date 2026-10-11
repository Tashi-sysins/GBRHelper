using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using SN = SmoothNav.Core;

namespace GBRHelper.Navigation;

/// <summary>
/// 角の曲線を SmoothNav の CurveBuilder（Navigation/SmoothNav の写し）で作るつなぎ口。
///
/// 前の版は角の内側を削る二次ベジェを独自に作っていた。公式の地上の経路は、身体の半径だけ縮めた面の縁（壁の角）で
/// 曲がるので、内側へ削った線は必ず面の外へ出る。面の照会（水平 0.05m の許容）で全部落ち、実機の記録
/// （10-12 0〜6 時）では地上の 25 経路が 1 つも曲線にならなかった。
/// CurveBuilder は角の外へ膨らむ円弧と S 字で曲がり、壁の近くでは面の縁から離す（ゲームで確かめた形）。
///
/// 地上の新しい線は vnavmesh の点の照会（MeshPointGround）で、飛行の新しい線は GBRHelper のゲームの当たり判定で確かめる。
/// 公式の線の上に乗る部分は公式と同じ線として通す（窓口の誤判定で公式より悪くしない）。
/// CurveBuilder は裏のスレッドで同期で照会するので、必ずフレームのスレッドの外から呼ぶ（呼び出し側が Task.Run で包む）。
/// </summary>
public static class SmoothNavCurve
{
    /// <summary>
    /// 角を曲線にした経路を返す。keepStart 個目までの点と、最後の区間（最後の 2 点）は GBR の経路のまま残す
    /// （騎乗の切り替え・最終接近・着地の切り替えの線を変えないため）。曲線にできなければ入力の写しを返す。
    /// </summary>
    public static List<Vector3> Round(IReadOnlyList<Vector3> input, bool fly, int keepStart, IRouteQueries queries,
        RouteSettings c, CancellationToken cancel)
    {
        var copy = input.ToList();
        var start = Math.Clamp(keepStart, 0, copy.Count);
        // 曲げる部分：start 個目から最後の 1 つ手前まで。角が 1 つも無い（3 点未満）なら何もしない。
        var body = copy.Skip(start).Take(copy.Count - 1 - start).ToList();
        if (body.Count < 3) return copy;

        IReadOnlyList<SN.ICollisionProbe> probes;
        SN.IGroundSurface? ground = null;
        if (fly) probes = [new QueriesProbe(queries, cancel)];
        else
        {
            if (queries is not SN.IMeshPointQuery mesh) return copy;
            probes = [];
            ground = new SN.MeshPointGround(mesh);
        }
        var settings = new SN.CurveSettings(Spacing: c.CurveSpacing, MaximumLengthRatio: c.CurveLengthRatio, MaximumPoints: c.MaxPoints);
        var result = SN.CurveBuilder.Build(body, fly, probes, settings, ground, cancel);
        var used = result.UsedPath.ToList();
        // 端点は必ず元のまま（念のため確かめる。違えば CurveBuilder の結果は使わない）。
        if (used.Count < 2 || used[0] != body[0] || used[^1] != body[^1] || used.Any(p => !Geometry.Finite(p))) used = body.ToList();
        // 2 段目：CurveBuilder が残した鋭い角（採取点へ近づく所の短い辺のジグザグ・物差しが良くならず公式を残した所）を小さく丸める。
        // 実機の記録の地上 25 経路をオフラインで通すと、CurveBuilder だけでは 30 度以上の角 14 個のうち 1 個しか丸まらなかった。
        Func<IReadOnlyList<(Vector3 From, Vector3 To)>, bool> clear = fly
            ? lines => probes[0] is SN.IBatchCollisionProbe batch && batch.Segments(lines, true).All(r => r.Verdict == SN.Verdict.Clear)
            : lines => lines.All(l => ground!.Segment(l.From, l.To, false).Verdict == SN.Verdict.Clear);
        used = RoundRemaining(used, c, clear, cancel);
        // CurveBuilder の細かい点の上に残った角は小さくしか丸められないので、元の経路をそのまま丸めた形とも比べ、
        // 一番鋭い角が小さい方を使う（同じなら CurveBuilder の形＝壁から離れ、外へ膨らむ方）。
        if (!used.SequenceEqual(body) && MaxTurn(used) >= 30 && !cancel.IsCancellationRequested)
        {
            var simple = RoundRemaining(body.ToList(), c, clear, cancel);
            if (MaxTurn(simple) + 1 < MaxTurn(used)) used = simple;
        }
        if (Geometry.Length(used) > Geometry.Length(body) * c.CurveLengthRatio + .01f || used.Count > c.MaxPoints) return copy;
        if (used.SequenceEqual(body)) return copy;
        var output = copy.Take(start).ToList();
        output.AddRange(used);
        output.Add(copy[^1]);
        return output;
    }

    /// <summary>
    /// 残った角（水平に MinimumTurn 度以上曲がる点）を、前後の辺に接する二次ベジェで丸める。端の点は変えない。
    /// 削る長さは前後の辺の 45% と 2m の小さい方（隣の角と重ならない）。新しい線が通らなければ 1/2・1/4 に縮め、それでも駄目なら角のまま。
    /// </summary>
    private static List<Vector3> RoundRemaining(List<Vector3> input, RouteSettings c, Func<IReadOnlyList<(Vector3 From, Vector3 To)>, bool> clear, CancellationToken cancel)
    {
        const float MinimumTurn = 20, MaximumTrim = 2, EdgeShare = .45f;
        if (input.Count < 3) return input;
        var output = new List<Vector3> { input[0] };
        for (var i = 1; i < input.Count - 1; i++)
        {
            if (cancel.IsCancellationRequested) { output.AddRange(input.Skip(i)); return output; }
            var incoming = input[i] - input[i - 1]; var outgoing = input[i + 1] - input[i];
            var left = incoming.Length(); var right = outgoing.Length();
            var turn = HorizontalTurn(incoming, outgoing);
            if (left < .1f || right < .1f || turn < MinimumTurn || turn > 120) { output.Add(input[i]); continue; }
            incoming /= left; outgoing /= right;
            var trim = Math.Min(MaximumTrim, Math.Min(left, right) * EdgeShare);
            List<Vector3>? accepted = null;
            foreach (var scale in new[] { 1f, .5f, .25f })
            {
                var a = input[i] - incoming * trim * scale; var b = input[i] + outgoing * trim * scale;
                var curve = Bezier(a, input[i], b, c.CurveSpacing);
                // 前の点（丸めた角の終わりのこともある）から曲線を通って次の点まで。
                var points = new List<Vector3> { output[^1] }; points.AddRange(curve); points.Add(input[i + 1]);
                var lines = Enumerable.Range(1, points.Count - 1).Select(k => (points[k - 1], points[k])).ToList();
                if (clear(lines)) { accepted = curve; break; }
            }
            if (accepted == null) output.Add(input[i]); else output.AddRange(accepted);
        }
        output.Add(input[^1]);
        return output;
    }
    // 点の並びの中で一番大きい水平の曲がり（度）。
    private static float MaxTurn(IReadOnlyList<Vector3> p)
    {
        var largest = 0f;
        for (var i = 1; i < p.Count - 1; i++) largest = Math.Max(largest, HorizontalTurn(p[i] - p[i - 1], p[i + 1] - p[i]));
        return largest;
    }
    private static float HorizontalTurn(Vector3 a, Vector3 b)
    {
        var u = new Vector2(a.X, a.Z); var v = new Vector2(b.X, b.Z);
        if (u.LengthSquared() < 1e-6f || v.LengthSquared() < 1e-6f) return 0;
        return MathF.Acos(Math.Clamp(Vector2.Dot(Vector2.Normalize(u), Vector2.Normalize(v)), -1, 1)) * 180 / MathF.PI;
    }
    // 二次ベジェ（a と b で元の辺に接する）を弧長でほぼ等間隔に取る。両端を含む。
    private static List<Vector3> Bezier(Vector3 a, Vector3 corner, Vector3 b, float spacing)
    {
        var dense = new List<Vector3>();
        for (var k = 0; k <= 32; k++) { var t = k / 32f; var s = 1 - t; dense.Add(s * s * a + 2 * s * t * corner + t * t * b); }
        // 短い角（短い辺のジグザグ）でも面取り 1 本にならないよう、1 つの曲線を最低 4 つに分ける。
        var total = 0f; for (var k = 1; k < dense.Count; k++) total += Vector3.Distance(dense[k - 1], dense[k]);
        spacing = Math.Max(.05f, Math.Min(spacing, total / 4));
        var result = new List<Vector3> { a }; var distance = 0f; var next = spacing;
        for (var k = 1; k < dense.Count; k++)
        {
            var length = Vector3.Distance(dense[k - 1], dense[k]);
            while (length > 0 && next < distance + length) { result.Add(Vector3.Lerp(dense[k - 1], dense[k], (next - distance) / length)); next += spacing; }
            distance += length;
        }
        if (result.Count > 1 && Vector3.Distance(result[^1], b) < spacing * .3f) result[^1] = b; else result.Add(b);
        return result;
    }

    /// <summary>GBRHelper の照会（フレームのスレッドの待ち行列）を、SmoothNav のまとめて頼める窓口の形にする。</summary>
    private sealed class QueriesProbe(IRouteQueries queries, CancellationToken cancel) : SN.IBatchCollisionProbe
    {
        public string Name => "ゲームの当たり判定（GBRHelper）";
        public SN.ProbeResult Segment(Vector3 from, Vector3 to, bool fly) => Segments([(from, to)], fly)[0];
        public SN.ProbeResult[] Segments(IReadOnlyList<(Vector3 From, Vector3 To)> segments, bool fly)
        {
            try
            {
                var answers = queries.CheckAsync(segments.Select(s => new Segment(s.From, s.To, fly)).ToList(), cancel)
                    .GetAwaiter().GetResult();
                return segments.Select((_, i) => i < answers.Length ? Map(answers[i]) : new SN.ProbeResult(SN.Verdict.Unknown, "照会の答えが足りない")).ToArray();
            }
            catch (Exception ex)
            {
                // 打ち切り・待ち行列の上限など。分からない線は使わない（公式の区間のまま）。
                return segments.Select(_ => new SN.ProbeResult(SN.Verdict.Unknown, "照会の中止: " + ex.GetType().Name)).ToArray();
            }
        }
        private static SN.ProbeResult Map(ProbeResult p) => new(p.Verdict switch
        {
            Verdict.Clear => SN.Verdict.Clear,
            Verdict.Blocked => SN.Verdict.Blocked,
            _ => SN.Verdict.Unknown,
        }, p.Reason, Point: p.Point, Normal: p.Normal);
    }
}
