using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace GBRHelper.Features;

/// <summary>
/// 帰還先の宿屋を扱う。一覧そのものは <see cref="InnCatalog"/> がゲームデータから導く。
/// ここでは「アクセス済みで飛べるか」「既定値」「旧設定の移行」を扱う。
///
/// 【コンテンツの中ではアクセス済みの一覧が空になる】
///   空を「1つもアクセスしていない」と読むと、行けるはずの宿屋へ
///   「アクセスしていない」と言って失敗する。
///   空のときは「まだ判断できない」として扱い、判定を先送りする。
///   この状態で誤判定すると、他の処理の最中に止まってしまう。
/// </summary>
public sealed class InnService
{
    /// <summary>宿屋の一覧。ゲームデータは起動中に変わらないので、一度作ったら持ち続ける。</summary>
    private List<InnDestination>? all;

    /// <summary>一覧から外した宿屋（都市が決まらなかったもの）。画面に出す。</summary>
    public IReadOnlyList<string> Skipped { get; private set; } = [];

    /// <summary>一覧を作れなかった理由。作れていれば空。</summary>
    public string BuildError { get; private set; } = string.Empty;

    /// <summary>アクセス済みの大エーテライト。毎フレーム引くと重いので少し持つ。</summary>
    private HashSet<uint>? attuned;
    private DateTime attunedExpiry = DateTime.MinValue;

    /// <summary>
    /// 宿屋の一覧（アクセスしていない都市の宿屋も含む）。
    /// 並びと番号は Lifestream の宿屋表と同じ。
    /// </summary>
    public IReadOnlyList<InnDestination> All()
    {
        if (this.all is not null)
            return this.all;

        try
        {
            var territories = Svc.Data.GetExcelSheet<TerritoryType>();
            var aetherytes = Svc.Data.GetExcelSheet<Aetheryte>();

            this.all = InnCatalog.Build(territories, aetherytes, out var skipped);
            this.Skipped = skipped;

            foreach (var s in skipped)
                Svc.Log.Warning($"[Inn] 都市を決められないため一覧から外しました: {s}");
        }
        catch (Exception ex)
        {
            // シートの列が変わった（パッチ直後）などで読めないとき。
            // プラグイン全体を落とさず、宿屋へ向かう機能だけを止める。
            this.BuildError = $"{ex.GetType().Name}: {ex.Message}";
            Svc.Log.Warning($"[Inn] 宿屋の一覧を作れませんでした: {this.BuildError}");
            this.all = [];
        }

        return this.all;
    }

    /// <summary>
    /// アクセス済みエーテライトの一覧を、いま信じてよいか。
    /// コンテンツ内では空になるため、空なら「まだ判断できない」とみなす。
    /// </summary>
    public bool IsListReady()
    {
        try
        {
            return Svc.Aetherytes.Any();
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[Inn] エーテライトの一覧を読めません: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// その宿屋の都市の大エーテライトにアクセス済みか（＝ Lifestream が飛べるか）。
    /// 一覧を読めないときは false。呼び出し側で <see cref="IsListReady"/> と組み合わせること。
    /// </summary>
    public bool IsAttuned(InnDestination inn)
    {
        var now = DateTime.UtcNow;

        if (this.attuned is null || now >= this.attunedExpiry)
        {
            var set = new HashSet<uint>();

            try
            {
                foreach (var entry in Svc.Aetherytes)
                    set.Add(entry.AetheryteId);
            }
            catch (Exception ex)
            {
                Svc.Log.Warning($"[Inn] エーテライトの一覧を取得できませんでした: {ex.Message}");
            }

            this.attuned = set;
            this.attunedExpiry = now.AddSeconds(5);
        }

        return this.attuned.Contains(inn.AetheryteId);
    }

    /// <summary>宿屋の部屋のエリア番号から探す。</summary>
    public InnDestination? Find(uint innTerritoryId)
        => InnCatalog.FindByInn(this.All(), innTerritoryId);

    /// <summary>そのエリアが宿屋の部屋か。</summary>
    public static bool IsInInn(uint territoryId)
    {
        try
        {
            return Svc.Data.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out var row)
                   && InnCatalog.IsInnRoom(row);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[Inn] エリアの種類を読めません: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 帰還先が未設定なら、既定の宿屋（リムサ）を選ぶ。
    ///
    /// 設定ファイルに固定値を書き込む形にはしない。
    /// リムサにアクセスしていないキャラクターでは飛べない値が入ってしまい、
    /// 実行時に「テレポートできません」で止まることになる。
    /// 実際にアクセス済みのときだけ選ぶ。
    ///
    /// 一覧はコンテンツ内では空になるため、読めないうちは何もしない。
    /// 次に読めたときに改めて試す。
    /// </summary>
    /// <returns>既定を適用したら true。</returns>
    public bool TryApplyDefault(Configuration config)
    {
        if (config.InnAuto || config.InnTerritoryId != 0)
            return false;

        if (!this.IsListReady())
            return false;

        if (InnCatalog.FindByCity(this.All(), InnCatalog.DefaultCityTerritory) is not { } inn)
            return false;

        if (!this.IsAttuned(inn))
            return false;

        config.InnTerritoryId = inn.InnTerritoryId;
        config.InnName = inn.Name;
        config.Save();

        return true;
    }

    /// <summary>
    /// 旧設定（版 1：都市のエーテライト）を、同じ都市の宿屋へ移す。
    ///
    /// 同じ都市かは PlaceNameZone で判断する（宿屋の部屋と都市で共通の値。
    /// リムサなら上甲板層・下甲板層・宿屋の部屋がすべて「リムサ・ロミンサ市街」）。
    /// ソリューション・ナインには宿屋が無いので、未設定に戻して既定（リムサ）に任せる。
    ///
    /// 旧設定の値は消さない。移行を誤ったときに元の選択を読み返せるようにするため。
    /// </summary>
    /// <returns>記録に残す一文。移行が要らなければ null。</returns>
    public string? MigrateFromCity(Configuration config)
    {
        if (config.Version >= Configuration.CurrentVersion)
            return null;

        // 一覧を作れていないのに移すと、どの都市も「宿屋が無い」と判断してしまう。
        // 版を上げずに残し、次に読み込んだときにやり直す。
        if (this.All().Count == 0)
            return $"宿屋の一覧を作れなかったため、以前の帰還先の移行を見送りました（{this.BuildError}）";

        string message;

        if (config.HomeAetheryteId == 0)
        {
            message = "設定を新しい形式にしました（以前の帰還先は未設定でした）";
        }
        else
        {
            var zone = ZoneOfAetheryte(config.HomeAetheryteId);
            var inn = InnCatalog.FindByZone(this.All(), zone);

            if (inn is { } found)
            {
                config.InnTerritoryId = found.InnTerritoryId;
                config.InnName = found.Name;
                message = $"帰還先を {config.HomeAetheryteName} から、同じ都市の宿屋 {found.Name} にしました";
            }
            else
            {
                message = $"以前の帰還先 {config.HomeAetheryteName} には宿屋が無いため、"
                          + "帰還先を未設定にしました（既定のリムサの宿屋が入ります）";
            }
        }

        config.Version = Configuration.CurrentVersion;
        config.Save();

        return message;
    }

    /// <summary>エーテライトのある区域の PlaceNameZone。読めなければ 0。</summary>
    private static uint ZoneOfAetheryte(uint aetheryteId)
    {
        try
        {
            if (!Svc.Data.GetExcelSheet<Aetheryte>().TryGetRow(aetheryteId, out var row))
                return 0;

            return row.Territory.ValueNullable?.PlaceNameZone.RowId ?? 0;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[Inn] 旧設定のエーテライトを読めません: {ex.Message}");
            return 0;
        }
    }
}
