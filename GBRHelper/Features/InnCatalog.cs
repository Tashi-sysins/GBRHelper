using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace GBRHelper.Features;

/// <summary>帰還先の宿屋1件ぶんの情報。</summary>
/// <param name="InnTerritoryId">宿屋の部屋のエリア番号（177 など）。設定に保存し、着いたかの判定にも使う。</param>
/// <param name="CityTerritoryId">
/// 宿屋のある都市のうち、大エーテライトがある区域のエリア番号（リムサなら下甲板層の 129）。
/// Lifestream の宿屋表のキーと同じもの。
/// </param>
/// <param name="AetheryteId">その都市の大エーテライトの行 ID。アクセス済みか（＝飛べるか）の判定に使う。</param>
/// <param name="ZoneId">PlaceNameZone の行 ID。宿屋の部屋と都市で共通の値になる。旧設定の移行に使う。</param>
/// <param name="LifestreamIndex">Lifestream.EnqueueLocalInnShortcut に渡す番号（0 始まり）。</param>
/// <param name="Name">画面に出す名前。シートから引く。</param>
public readonly record struct InnDestination(
    uint InnTerritoryId,
    uint CityTerritoryId,
    uint AetheryteId,
    uint ZoneId,
    int LifestreamIndex,
    string Name);

/// <summary>
/// 宿屋の一覧をゲームデータから導く。ゲームには触らない（シートを読むだけ）ので、ゲーム無しで試せる。
///
/// 【宿屋の番号を埋め込まない理由】
///   Lifestream の宿屋機能は、宿屋を「InnData の何番目か」で受け取る。
///   InnData は都市のエリア番号をキーにした SortedDictionary で、
///   Lifestream のソースに 8 都市が直接書かれている
///   （Lifestream/Tasks/Shortcuts/TaskPropertyShortcut.cs:25-35）。
///   こちらが番号を書き写すと、パッチで宿屋が増えたときに黙ってずれる。
///   同じ一覧をゲームデータから導き、同じ並べ方（エリア番号の昇順）で番号を振る。
///
/// 【導き方】（ver 2026.09.15.0000.0000 で実測し、Lifestream の 8 都市と並び順まで一致した）
///   1. TerritoryType のうち、用途（TerritoryIntendedUse）が宿屋の部屋のもの … 8 件
///   2. 同じ PlaceNameZone を持つ区域のうち、大エーテライト（Aetheryte.IsAetheryte）がある区域
///      … どの宿屋でもちょうど 1 区域（リムサなら上甲板層 128 には無く、下甲板層 129 にある）
///   3. その区域の大エーテライトは、Lifestream と同じく行番号の一番小さいもの
///      （Lifestream は First(x => x.IsAetheryte && x.Territory.RowId == 都市) で引いている）
///
/// ソリューション・ナインには宿屋の部屋が無い（同じ PlaceNameZone の宿屋が存在しない）。
/// </summary>
public static class InnCatalog
{
    /// <summary>
    /// 宿屋の部屋を表す TerritoryIntendedUse の行番号。
    ///
    /// この値の名前はシートに無い（TerritoryIntendedUse シートは名前の列を持たない）。
    /// ECommons の TerritoryIntendedUseEnum.Inn = 2 と、Lifestream の判定
    /// （Utils.cs:1508）が同じ値を使っている。ゲームデータでも、この値を持つ区域は
    /// 8 件ですべて宿屋の部屋だった（177/178/179/429/629/843/990/1205）。
    /// </summary>
    public const uint InnIntendedUse = 2;

    /// <summary>
    /// 帰還先の既定にする都市（リムサ・ロミンサ：下甲板層）。
    ///
    /// 指定「既定はリムサ」。一覧の先頭を既定にしないのは、
    /// アクセス状況によって既定の宿屋が変わってしまうため。
    /// </summary>
    public const uint DefaultCityTerritory = 129;

    /// <summary>その区域が宿屋の部屋か。</summary>
    public static bool IsInnRoom(TerritoryType row)
        => row.TerritoryIntendedUse.RowId == InnIntendedUse;

    /// <summary>
    /// 宿屋の一覧を作る。並びは Lifestream の宿屋表と同じ（都市のエリア番号の昇順）。
    /// </summary>
    /// <param name="territories">TerritoryType シートの全行。</param>
    /// <param name="aetherytes">Aetheryte シートの全行。</param>
    /// <param name="skipped">都市を1つに決められず外した宿屋（画面と記録に出す）。</param>
    public static List<InnDestination> Build(
        IEnumerable<TerritoryType> territories,
        IEnumerable<Aetheryte> aetherytes,
        out List<string> skipped)
    {
        skipped = [];

        var all = territories.ToList();

        // 区域ごとの大エーテライト（行番号の小さい順）。
        var mainByTerritory = aetherytes
            .Where(a => a.IsAetheryte)
            .GroupBy(a => a.Territory.RowId)
            .ToDictionary(g => g.Key, g => g.Min(a => a.RowId));

        var found = new List<(TerritoryType Inn, uint City, uint Aetheryte)>();

        foreach (var inn in all.Where(IsInnRoom))
        {
            var zone = inn.PlaceNameZone.RowId;

            var cities = all
                .Where(t => t.RowId != inn.RowId
                            && t.PlaceNameZone.RowId == zone
                            && mainByTerritory.ContainsKey(t.RowId))
                .Select(t => t.RowId)
                .ToList();

            // 都市が決まらない宿屋は外す。推測で選ぶと、Lifestream と番号がずれる。
            if (cities.Count != 1)
            {
                skipped.Add($"{PlaceNameOf(inn)}（エリア {inn.RowId}・大エーテライトのある区域が {cities.Count} 件）");
                continue;
            }

            found.Add((inn, cities[0], mainByTerritory[cities[0]]));
        }

        // Lifestream の宿屋表は都市のエリア番号の昇順（SortedDictionary）。
        // 番号はその並びの何番目か。
        return found
            .OrderBy(x => x.City)
            .Select((x, i) => new InnDestination(
                x.Inn.RowId,
                x.City,
                x.Aetheryte,
                x.Inn.PlaceNameZone.RowId,
                i,
                DisplayName(x.Inn)))
            .ToList();
    }

    /// <summary>都市のエリア番号から宿屋を探す。</summary>
    public static InnDestination? FindByCity(IEnumerable<InnDestination> inns, uint cityTerritoryId)
        => inns.Cast<InnDestination?>().FirstOrDefault(x => x!.Value.CityTerritoryId == cityTerritoryId);

    /// <summary>宿屋の部屋のエリア番号から探す。</summary>
    public static InnDestination? FindByInn(IEnumerable<InnDestination> inns, uint innTerritoryId)
        => inns.Cast<InnDestination?>().FirstOrDefault(x => x!.Value.InnTerritoryId == innTerritoryId);

    /// <summary>
    /// PlaceNameZone から宿屋を探す。旧設定（都市のエーテライト）の移行に使う。
    /// ソリューション・ナインのように宿屋の無い都市では null。
    /// </summary>
    public static InnDestination? FindByZone(IEnumerable<InnDestination> inns, uint zoneId)
        => zoneId == 0
            ? null
            : inns.Cast<InnDestination?>().FirstOrDefault(x => x!.Value.ZoneId == zoneId);

    /// <summary>「リムサ・ロミンサ市街（宿屋「ミズンマスト」）」の形。</summary>
    private static string DisplayName(TerritoryType inn)
    {
        var zone = inn.PlaceNameZone.ValueNullable?.Name.ExtractText();
        var name = PlaceNameOf(inn);

        return string.IsNullOrEmpty(zone) ? name : $"{zone}（{name}）";
    }

    private static string PlaceNameOf(TerritoryType row)
    {
        var name = row.PlaceName.ValueNullable?.Name.ExtractText();
        return string.IsNullOrEmpty(name) ? $"エリア {row.RowId}" : name;
    }
}
