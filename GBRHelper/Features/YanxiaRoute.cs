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
/// 【北東の尾根（要望で足した）】北側（烈士庵・蓮根・パーライトなど）から東の松脂・翠銀鉱へ向かう飛行の経路は、
///   北東の尾根 (466〜482, −474〜−514) とその奥 (605〜607, −591〜−592) で地形を突き抜ける。ナマイ村から向かう経路はどれも突き抜けない（同じ調べ）。
/// 【判断】GBR の経路（いまの場所から vnavmesh の残りの点まで）が帯（Zones）を通り、行き先が帯の外なら、
///   行き先へまっすぐ向かう線が帯を通らないエーテライトのうち、行き先に近い方へテレポする（PickAetheryte）。
///   ただし、そのエーテライトのそば（150m）に既にいるとき・アクセスしていないときは飛ばない。
///   山と川の帯は、テレポした方が行き先から遠くなるときも飛ばない（0.3.0.10 のまま）。北東の尾根は遠くなっても飛ぶ（Zone.RequireCloser の説明）。
///   向きごとに条件を書かない（北 → 南も、南 → 北も同じ判定。向きごとに書くと片方だけ直し忘れる）。
///   採集物も見ない（見るのは経路だけ。同じ経路を通る採集物が増えても手を入れずに済む）。
/// 【前の決め方をやめた理由】最初は「烈士庵とナマイ村のどちらに近いかで側を決め、側が変わって 100m 以上縮めば飛ぶ」にしたが、
///   調べると ①東の松脂の採集点はどちらからも 350〜390m で、1m の差で側が分かれる ②東の松脂 → 蓮根・パーライトは川で地形を突き抜けるのに、
///   縮む距離が 52〜130m で飛ばない ③大根 → 南西の印刷必需品素材は山を通らないのに 129m 縮むので飛ぶ、だった。経路そのものが帯を通るかで見る。
///   飛び先も「行き先に近い方」だけでは、北の松脂（烈士庵に 1m だけ近い）で烈士庵を選び、烈士庵からでも北東の尾根で突き抜ける（2026-10-07 調べ）ので、
///   行き先までのあいだに帯が無い方を先に選ぶ。
/// </summary>
public static class YanxiaRoute
{
    /// <summary>
    /// 飛行の経路が通ると地形を突き抜ける場所（XZ の四角。高さは見ない＝飛んで越えようとしても尾根の下を通るため）。
    /// </summary>
    /// <param name="RequireCloser">
    /// テレポした方が行き先に近いときだけ飛ぶか。山と川の帯は true（0.3.0.10 のまま。帯を通る経路はどれもテレポした方が近かった）。
    /// 北東の尾根は false：北側から東の松脂・翠銀鉱へは、北側のどこから飛んでも突き抜け、ナマイ村から飛ぶ経路だけが突き抜けない（2026-10-07 調べ）。
    /// インペリアルジェード原石・パーライトから東の松脂へは、ナマイ村から飛ぶ方が 5〜95m 遠いが、そのまま飛ぶと詰まる。
    /// </param>
    public sealed record Zone(string Name, float MinX, float MaxX, float MinZ, float MaxZ, bool RequireCloser)
    {
        public bool Contains(Vector3 p) => p.X >= MinX && p.X <= MaxX && p.Z >= MinZ && p.Z <= MaxZ;
    }

    /// <summary>
    /// 山と川の帯。烈士庵のエーテライト (245, −404) は外（西の端の外）。北の採集点（蓮根・パーライト・改良用のダークチェスナット原木・インペリアルジェード原石）と、
    /// 南の採集点・ナマイ村・東の松脂は、どれも外（2026-10-07 調べ）。
    /// </summary>
    public static readonly Zone MountainBand = new("山と川の帯", 250f, 480f, -390f, -265f, RequireCloser: true);

    /// <summary>
    /// 北東の尾根（尾根とその奥と、その南の山）。ヤンサの採集点・エーテライトはどれも外（2026-10-09 調べ）。
    /// 南の端（Z −395）まで広げたのは、烈士庵から北の松脂へのまっすぐの線（Z −404〜−423）がこの山（高さ 240m 以上か地形の当たりの無い塊）の上を通り、
    /// 実際の経路は北へ回って尾根で突き抜けるため（飛び先を選ぶとき、烈士庵を外せるように）。ナマイ村から北の松脂への線は X 617 まで Z −395 に届かない。
    /// 西の端（X 455）は、北のインペリアルジェード原石の採集点の範囲（ExportedGatheringPoint の基 500：中心 (418, −607)・半径 30）にかからないように
    /// （かかると、そこに湧いた採集点から北側どうしで動くときに「いまの場所が帯の中」になって飛んでしまう）。
    /// 南の岩清水から川の谷を低く（高さ 24〜36m）北へ抜ける経路も、この帯の南西の角を通る。地形は突き抜けないが、烈士庵へ飛ぶ方が行き先まで 200〜470m で、
    /// そのまま飛ぶ 630〜925m より近いので、そのままにした（ヤンサの採集点 48 か所の経路 2,450 本のうち 4 本。2026-10-09 調べ）。
    /// </summary>
    public static readonly Zone NortheastRidge = new("北東の尾根", 455f, 600f, -620f, -395f, RequireCloser: false);

    /// <summary>判断に使う帯の全部。</summary>
    public static readonly IReadOnlyList<Zone> Zones = [MountainBand, NortheastRidge];

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
        => Decide(me, waypoints, aetherytes, Zones);

    /// <summary>帯を指定して判断する（調べの道具で、帯の候補を比べるため）。</summary>
    public static Decision Decide(Vector3 me, IReadOnlyList<Vector3> waypoints, IReadOnlyList<Aetheryte> aetherytes, IReadOnlyList<Zone> zones)
    {
        if (aetherytes.Count == 0)
            return new(null, "エーテライトの位置が分かりません");
        if (waypoints.Count == 0)
            return new(null, "いま経路が立っていません");
        if (FirstZonePassed(me, waypoints, zones) is not { } zone)
            return new(null, $"経路は{string.Join("・", zones.Select(z => z.Name))}を通りません");
        var dest = waypoints[^1];
        if (zones.FirstOrDefault(z => z.Contains(dest)) is { } inside)
            return new(null, $"行き先が{inside.Name}の中です");
        return Toward(me, dest, aetherytes, zone.RequireCloser, zones, zone.Name);
    }

    /// <summary>
    /// 行き先へ向かうのによいエーテライトへ飛ぶべきか（詰まりを見つけたとき。requireCloser＝テレポした方が行き先に近いときだけ）。
    /// </summary>
    public static Decision Toward(Vector3 me, Vector3 dest, IReadOnlyList<Aetheryte> aetherytes, bool requireCloser)
        => Toward(me, dest, aetherytes, requireCloser, Zones, null);

    private static Decision Toward(Vector3 me, Vector3 dest, IReadOnlyList<Aetheryte> aetherytes, bool requireCloser,
        IReadOnlyList<Zone> zones, string? crossing)
    {
        if (aetherytes.Count == 0)
            return new(null, "エーテライトの位置が分かりません");
        var here = Flat(me); var there = Flat(dest);
        var target = PickAetheryte(dest, aetherytes, zones);
        var meToTarget = Vector2.Distance(here, target.Position);
        if (meToTarget <= NearRange)
            return new(null, $"もう {target.Name} のそば（{meToTarget:F0}m）です");
        if (!target.Attuned)
            return new(null, $"行き先へは {target.Name} から向かいますが、{target.Name} にアクセスしていません");
        var direct = Vector2.Distance(here, there);
        var viaTeleport = Vector2.Distance(target.Position, there);
        if (requireCloser && viaTeleport >= direct)
            return new(null, $"{target.Name} へテレポしても行き先に近づきません（いまの場所から {direct:F0}m・{target.Name} から {viaTeleport:F0}m）");
        var distances = $"行き先まで いまの場所から {direct:F0}m・{target.Name} から {viaTeleport:F0}m";
        return new(target, crossing is null ? distances : $"{crossing}を越える経路（{distances}）");
    }

    /// <summary>
    /// 行き先へ向かうのに使うエーテライト：そこから行き先へまっすぐ向かう線が帯を通らないもののうち、行き先に近いもの。
    /// どれも帯を通るなら、行き先に近いもの（0.3.0.10 までの選び方）。
    /// </summary>
    public static Aetheryte PickAetheryte(Vector3 dest, IReadOnlyList<Aetheryte> aetherytes, IReadOnlyList<Zone> zones)
    {
        var there = Flat(dest);
        var byDistance = aetherytes.OrderBy(a => Vector2.DistanceSquared(a.Position, there)).ToList();
        return byDistance.FirstOrDefault(a => FirstZonePassed(new Vector3(a.Position.X, dest.Y, a.Position.Y), [dest], zones) is null)
               ?? byDistance[0];
    }

    /// <summary>その点が帯のどれかの中か（XZ）。</summary>
    public static bool InZone(Vector3 p) => Zones.Any(z => z.Contains(p));

    /// <summary>いまの場所から経路の点をたどる折れ線が、帯のどれかを通るか。</summary>
    public static bool PassesZone(Vector3 me, IReadOnlyList<Vector3> waypoints) => FirstZonePassed(me, waypoints, Zones) is not null;

    /// <summary>いまの場所から経路の点をたどる折れ線が最初に通る帯（2m おきに見る。経路の点は多くて数十）。通らなければ null。</summary>
    public static Zone? FirstZonePassed(Vector3 me, IReadOnlyList<Vector3> waypoints, IReadOnlyList<Zone> zones)
    {
        var prev = me;
        if (ZoneAt(prev, zones) is { } start) return start;
        foreach (var next in waypoints)
        {
            var steps = Math.Max(1, (int)(Vector2.Distance(Flat(prev), Flat(next)) / 2f));
            for (var s = 1; s <= steps; s++)
                if (ZoneAt(Vector3.Lerp(prev, next, (float)s / steps), zones) is { } hit) return hit;
            prev = next;
        }
        return null;
    }

    private static Zone? ZoneAt(Vector3 p, IReadOnlyList<Zone> zones)
    {
        foreach (var z in zones)
            if (z.Contains(p)) return z;
        return null;
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
