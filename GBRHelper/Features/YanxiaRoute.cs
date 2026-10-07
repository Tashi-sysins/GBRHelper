using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace GBRHelper.Features;

/// <summary>
/// ヤンサの山越えの判断（ゲームに触らない部分。試験できるように分けた。動かすのは YanxiaShortcut）。
///
/// 【なぜ要るか】ヤンサの烈士庵とナマイ村のあいだには山と川があり、vnavmesh の飛行の経路がそこで地形を突き抜けて引かれる。
///   調べ（【道具】ゲーム無しの調査\地図の一点を調べる・2026-10-07）：
///   ・利用者が詰まった (270.5, 138.1, -292.4) は地形の山の斜面（地形の面 Y 138.42。周り 15m に歩ける面は無い）。
///     烈士庵 → ナマイ村の飛行の経路は (280,126,-304) → (433,72,-91) の 263m の直線で、この山の尾根の下を通る。
///   ・南 → 北（岩清水・東の松脂 → 蓮根・パーライト・大根・烈士庵など）の飛行の経路は、山のふもとの川 (393〜414, 0, −280〜−306) で地形を突き抜ける。
///   ・9 月に詰まった 2 か所 (307.5, −373)・(276.5, −380) も同じ帯にある。
///   ・地上の経路は、烈士庵 → ナマイ村が西へ大回りして 2,211m（直線は 371m）。
///   GBR は同じエリアの中では必ず飛ぶか歩く（別のエリアのときだけテレポする。GBR の AutoGather.cs 1341-1384）ので、外から助ける。
/// 【判断】GBR の経路（いまの場所から vnavmesh の残りの点まで）がこの山と川の帯（Zone）を通り、行き先が帯の外なら、
///   行き先に近い方のエーテライトへテレポする。ただし、そのエーテライトのそば（150m）に既にいるとき・アクセスしていないとき・
///   テレポした方が行き先から遠くなるときは飛ばない。
///   向きごとに条件を書かない（北 → 南も、南 → 北も同じ判定。向きごとに書くと片方だけ直し忘れる）。
///   採集物も見ない（見るのは経路だけ。同じ経路を通る採集物が増えても手を入れずに済む）。
/// 【前の決め方をやめた理由】最初は「烈士庵とナマイ村のどちらに近いかで側を決め、側が変わって 100m 以上縮めば飛ぶ」にしたが、
///   調べると ①東の松脂の採集点はどちらからも 350〜390m で、1m の差で側が分かれる ②東の松脂 → 蓮根・パーライトは川で地形を突き抜けるのに、
///   縮む距離が 52〜130m で飛ばない ③大根 → 南西の印刷必需品素材は山を通らないのに 129m 縮むので飛ぶ、だった。経路そのものが帯を通るかで見る。
/// </summary>
public static class YanxiaRoute
{
    /// <summary>
    /// 山と川の帯（XZ の四角。高さは見ない＝飛んで越えようとしても尾根の下を通るため）。
    /// 烈士庵のエーテライト (245, −404) は外（西の端の外）。北の採集点（蓮根・パーライト・改良用のダークチェスナット原木・インペリアルジェード原石）と、
    /// 南の採集点・ナマイ村・東の松脂は、どれも外（2026-10-07 調べ）。
    /// </summary>
    public const float ZoneMinX = 250f, ZoneMaxX = 480f, ZoneMinZ = -390f, ZoneMaxZ = -265f;

    /// <summary>着いたとみなす・そばにいるとみなす、エーテライトからの距離（m。9 月の版と同じ）。</summary>
    public const float NearRange = 150f;

    /// <summary>エーテライト（位置は XZ。高さは見ない）。Attuned＝アクセス済み（テレポできる）。</summary>
    public sealed record Aetheryte(uint Id, string Name, Vector2 Position, bool Attuned);

    /// <summary>判断の結果。Target が null なら、テレポしない（Reason にその理由）。</summary>
    public sealed record Decision(Aetheryte? Target, string Reason);

    /// <summary>
    /// いまの場所 me から、経路 waypoints（vnavmesh の Path.ListWaypoints。最後の点が行き先）で向かうとき、テレポすべきか。
    /// </summary>
    public static Decision Decide(Vector3 me, IReadOnlyList<Vector3> waypoints, IReadOnlyList<Aetheryte> aetherytes)
    {
        if (aetherytes.Count == 0)
            return new(null, "エーテライトの位置が分かりません");
        if (waypoints.Count == 0)
            return new(null, "いま経路が立っていません");
        if (!PassesZone(me, waypoints))
            return new(null, "経路は山と川の帯を通りません");
        var dest = waypoints[^1];
        if (InZone(dest))
            return new(null, "行き先が山と川の帯の中です");
        return Toward(me, dest, aetherytes, requireCloser: true);
    }

    /// <summary>
    /// 行き先に近い方のエーテライトへ飛ぶべきか（詰まりを見つけたとき。requireCloser＝テレポした方が行き先に近いときだけ）。
    /// </summary>
    public static Decision Toward(Vector3 me, Vector3 dest, IReadOnlyList<Aetheryte> aetherytes, bool requireCloser)
    {
        if (aetherytes.Count == 0)
            return new(null, "エーテライトの位置が分かりません");
        var here = Flat(me); var there = Flat(dest);
        var target = aetherytes.OrderBy(a => Vector2.DistanceSquared(a.Position, there)).First();
        var meToTarget = Vector2.Distance(here, target.Position);
        if (meToTarget <= NearRange)
            return new(null, $"もう {target.Name} のそば（{meToTarget:F0}m）です");
        if (!target.Attuned)
            return new(null, $"行き先は {target.Name} の側ですが、{target.Name} にアクセスしていません");
        var direct = Vector2.Distance(here, there);
        var viaTeleport = Vector2.Distance(target.Position, there);
        if (requireCloser && viaTeleport >= direct)
            return new(null, $"{target.Name} へテレポしても行き先に近づきません（いまの場所から {direct:F0}m・{target.Name} から {viaTeleport:F0}m）");
        return new(target, $"山と川の帯を越える経路（行き先まで いまの場所から {direct:F0}m・{target.Name} から {viaTeleport:F0}m）");
    }

    /// <summary>その点が山と川の帯の中か（XZ）。</summary>
    public static bool InZone(Vector3 p) => p.X is >= ZoneMinX and <= ZoneMaxX && p.Z is >= ZoneMinZ and <= ZoneMaxZ;

    /// <summary>いまの場所から経路の点をたどる折れ線が、山と川の帯を通るか（2m おきに見る。経路の点は多くて数十）。</summary>
    public static bool PassesZone(Vector3 me, IReadOnlyList<Vector3> waypoints)
    {
        var prev = me;
        if (InZone(prev)) return true;
        foreach (var next in waypoints)
        {
            var steps = Math.Max(1, (int)(Vector2.Distance(Flat(prev), Flat(next)) / 2f));
            for (var s = 1; s <= steps; s++)
                if (InZone(Vector3.Lerp(prev, next, (float)s / steps))) return true;
            prev = next;
        }
        return false;
    }

    /// <summary>
    /// 地図の印（MapMarker の X・Y＝地図の画像の上の位置）を、世界の座標の XZ に直す。
    /// 画像の位置 p と世界の座標 w の関係は p = (w + Offset) × (SizeFactor / 100) + 1024（Dalamud の MapUtil と同じ式）。
    /// </summary>
    public static Vector2 MarkerToWorld(int markerX, int markerY, ushort sizeFactor, short offsetX, short offsetY)
    {
        var scale = sizeFactor / 100f;
        return new((markerX - 1024f) / scale - offsetX, (markerY - 1024f) / scale - offsetY);
    }

    private static Vector2 Flat(Vector3 p) => new(p.X, p.Z);
}
