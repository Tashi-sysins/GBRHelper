using System;
using System.Collections.Generic;
using System.Linq;
using GBRHelper.Ipc;
using Lumina.Excel.Sheets;

namespace GBRHelper.Features;

/// <summary>
/// 検証のため、指定した宿屋へ移動する（デバッグ用）。
///
/// グリダニアの宿屋で回収の流れが動くことは確認できたので、ほかの宿屋にも
/// Lifestream が入れるかを、ベンチャーが溜まるのを待たずに確かめるためのもの。
///
/// 回収の流れ（RelayController）と同じ部品で動かす:
///   宿屋へ向かう判断 … InnTrip（回収と同じ判断。違うのは「指定の宿屋に着いたか」だけを数える点）
///   呼び鈴           … BellRunner（回収と同じ。この検証専用のインスタンスを持つ）
/// 同じ部品を通すので、ここで通れば回収のときも同じ経路を通る。
///
/// 【触らないもの】GatherBuddyReborn には触らない。
/// 回収の流れが動いている間（GBR の自動採集が ON）は使えないようにし、
/// 検証の途中で ON になったら検証のほうを止める（Lifestream と呼び鈴を取り合わないため）。
/// </summary>
public sealed class InnTestRunner : IDisposable
{
    /// <summary>1件の結果。</summary>
    public enum Outcome
    {
        /// <summary>指定の宿屋に入れた（呼び鈴も確かめたなら、それも通った）。</summary>
        Success,

        /// <summary>入れなかった、または呼び鈴で失敗した。</summary>
        Failed,

        /// <summary>試さなかった（未アクセス・既にその宿屋に居る・中止）。</summary>
        Skipped,
    }

    /// <param name="InnTerritoryId">試した宿屋の部屋のエリア番号。</param>
    /// <param name="Name">宿屋の名前。</param>
    /// <param name="At">終わった時刻（ローカル時刻）。</param>
    /// <param name="Outcome">結果。</param>
    /// <param name="Detail">説明。失敗したときは理由と、そのとき居た場所。</param>
    /// <param name="Travel">Lifestream が受け付けてから宿屋に入るまでの時間。入れなかったら null。</param>
    /// <param name="SettleGap">暗転の隙間（InnTrip.LongestSettleWait）。入れなかったら null。</param>
    public sealed record Result(
        uint InnTerritoryId,
        string Name,
        DateTime At,
        Outcome Outcome,
        string Detail,
        TimeSpan? Travel,
        TimeSpan? SettleGap);

    private enum Stage
    {
        Idle,
        Moving,
        Bell,
    }

    private readonly LifestreamIpc lifestream;
    private readonly InnService inns;
    private readonly BellRunner bell;
    private readonly RunLog log;

    private readonly InnTrip trip = new();
    private readonly Queue<InnDestination> queue = new();
    private readonly Dictionary<uint, Result> results = [];

    private Stage stage = Stage.Idle;
    private InnDestination current;
    private DateTime? acceptedAt;
    private TimeSpan? travel;
    private TimeSpan? settleGap;
    private bool openBell;

    public InnTestRunner(LifestreamIpc lifestream, InnService inns, BellRunner bell, RunLog log)
    {
        this.lifestream = lifestream;
        this.inns = inns;
        this.bell = bell;
        this.log = log;
    }

    /// <summary>検証が動いているか。</summary>
    public bool Running => this.stage != Stage.Idle;

    /// <summary>いま試している宿屋。動いていなければ null。</summary>
    public InnDestination? Current => this.Running ? this.current : null;

    /// <summary>このあと試す宿屋の数（いま試している分は含まない）。</summary>
    public int Remaining => this.queue.Count;

    /// <summary>画面に出す一行説明。分岐には使わない。</summary>
    public string Detail { get; private set; } = string.Empty;

    /// <summary>宿屋ごとの直近の結果（宿屋の部屋のエリア番号 → 結果）。</summary>
    public IReadOnlyDictionary<uint, Result> Results => this.results;

    /// <summary>
    /// 試す順番を決める。ゲームに触らないので、ゲーム無しで試せる。
    ///
    /// いま居る宿屋は最後に回す。居るままでは「移動できるか」を確かめられないが、
    /// ほかの宿屋へ移ったあとなら確かめられる。
    /// </summary>
    public static List<InnDestination> Order(IEnumerable<InnDestination> targets, uint here)
    {
        var list = targets.ToList();
        return list.Where(x => x.InnTerritoryId != here)
            .Concat(list.Where(x => x.InnTerritoryId == here))
            .ToList();
    }

    /// <summary>
    /// 検証を始める。
    /// </summary>
    /// <param name="targets">試す宿屋（1件でも複数でもよい）。</param>
    /// <param name="openBell">着いたら呼び鈴を開くところまで確かめるか。</param>
    public Func<bool> StartBlocked { get; set; } = () => false;
    public static bool Blocked(bool relayActive, bool? autoGather) => relayActive || autoGather != false;

    public void ResetCharacter()
    {
        stage = Stage.Idle; queue.Clear(); results.Clear(); trip.Reset(); bell.Reset();
        acceptedAt = null; travel = null; settleGap = null; Detail = "";
    }

    public void Start(IEnumerable<InnDestination> targets, bool openBell)
    {
        if (StartBlocked()) return;
        if (this.Running)
            return;

        this.queue.Clear();

        foreach (var t in Order(targets, Svc.ClientState.TerritoryType))
            this.queue.Enqueue(t);

        this.openBell = openBell;
        this.log.Write("検証", $"宿屋への移動の検証を始めます（{this.queue.Count} 件・呼び鈴{(openBell ? "も確かめる" : "は開かない")}）");
        this.Next();
    }

    /// <summary>
    /// 検証を止める。いま試している宿屋は「中止」として残す。
    /// 自分が頼んだ移動が Lifestream で走っていれば止める（頼んでいなければ触らない）。
    /// </summary>
    public void Cancel(string reason)
    {
        if (!this.Running)
            return;

        if (this.trip.Requested && this.lifestream.IsLoaded)
            this.lifestream.TryAbort();

        this.bell.Cancel(reason);
        this.Record(Outcome.Skipped, $"中止しました（{reason}）");

        var skipped = this.queue.Count;
        this.queue.Clear();
        this.trip.Reset();
        this.stage = Stage.Idle;
        this.Detail = $"中止しました（{reason}）";
        this.log.Write("検証", $"検証を中止しました（{reason}）。残り {skipped} 件は試していません");
    }

    /// <summary>毎フレーム呼ぶ。</summary>
    /// <param name="relayActive">回収の流れが動いているか（GBR の自動採集が ON）。</param>
    public void Tick(bool relayActive)
    {
        if (!this.Running)
            return;

        if (relayActive)
        {
            this.Cancel("GBR の自動採集が ON になったため。回収の流れと Lifestream・呼び鈴を取り合わないよう止めます");
            return;
        }

        if (!Me.Available)
            return;

        switch (this.stage)
        {
            case Stage.Moving:
                this.TickMoving();
                break;

            case Stage.Bell:
                this.TickBell();
                break;
        }
    }

    // ------------------------------------------------------------------

    /// <summary>次の宿屋へ進む。試せないものは理由を残して飛ばす。</summary>
    private void Next()
    {
        while (this.queue.Count > 0)
        {
            var target = this.queue.Dequeue();
            this.current = target;

            if (!this.inns.IsAttuned(target))
            {
                this.Record(Outcome.Skipped, "都市の大エーテライトにアクセスしていないため、試していません");
                continue;
            }

            if (Svc.ClientState.TerritoryType == target.InnTerritoryId)
            {
                this.Record(Outcome.Skipped,
                    "すでにこの宿屋に居るため、移動を確かめられません（ほかの場所から試してください）");
                continue;
            }

            this.trip.Reset();
            this.acceptedAt = null;
            this.travel = null;
            this.settleGap = null;
            this.stage = Stage.Moving;
            this.Detail = $"{target.Name} へ向かいます";
            this.log.Write("検証", $"{target.Name}（エリア {target.InnTerritoryId}・Lifestream の番号 {target.LifestreamIndex}）へ向かいます");
            return;
        }

        this.stage = Stage.Idle;
        this.Detail = "検証が終わりました";

        var ok = this.results.Values.Count(x => x.Outcome == Outcome.Success);
        var ng = this.results.Values.Count(x => x.Outcome == Outcome.Failed);
        var skip = this.results.Values.Count(x => x.Outcome == Outcome.Skipped);
        this.log.Write("検証", $"検証が終わりました（これまでの結果: 成功 {ok}・失敗 {ng}・試していない {skip}）");
    }

    private void TickMoving()
    {
        var now = DateTime.UtcNow;
        var territory = Svc.ClientState.TerritoryType;

        bool? busy = null;

        if (this.lifestream.IsLoaded && this.lifestream.TryIsBusy(out var b))
            busy = b;

        // 回収と違い、「指定の宿屋に居るか」だけを数える。
        // 違う宿屋に着いたときは、宿屋に入っていない扱いになり、猶予のあと失敗として残る。
        var input = new InnTrip.Input(
            InInn: territory == this.current.InnTerritoryId,
            Transitioning: RelayController.IsTransitioning(),
            LifestreamLoaded: this.lifestream.IsLoaded,
            LifestreamBusy: busy,
            Gathering: RelayController.IsGathering,
            UnsafeReason: RelayController.IsUnsafeToBreak(out var unsafeReason) ? unsafeReason : null,
            DestinationReady: this.inns.IsListReady());

        var decision = this.trip.Step(input, now);
        this.Detail = $"{this.current.Name}: {decision.Detail}";

        switch (decision.Action)
        {
            case InnTrip.Action.Request:
                this.Request(now);
                break;

            case InnTrip.Action.Arrived:
                this.travel = this.acceptedAt is { } at ? now - at : null;
                this.settleGap = this.trip.LongestSettleWait;
                this.log.Write("検証",
                    $"{this.current.Name} に入りました（{this.travel?.TotalSeconds:F0} 秒・暗転の隙間 {this.settleGap?.TotalSeconds:F1} 秒）");
                this.trip.Reset();
                this.AfterArrival();
                break;

            case InnTrip.Action.Fail:
                if (decision.AbortLifestream && this.trip.Requested && this.lifestream.IsLoaded)
                    this.lifestream.TryAbort();

                this.trip.Reset();
                this.Record(Outcome.Failed, $"{decision.Detail}（いまの場所: {DescribeTerritory(territory)}）");
                this.Next();
                break;
        }
    }

    private void Request(DateTime now)
    {
        if (!this.lifestream.TryEnqueueLocalInn(this.current.LifestreamIndex))
        {
            this.trip.Reset();
            this.Record(Outcome.Failed, "Lifestream に宿屋への移動を頼めませんでした（通信の失敗は「必要なプラグイン」の画面に出ます）");
            this.Next();
            return;
        }

        // 受け付けたかは直後の IsBusy で分かる（回収と同じ）。
        var accepted = this.lifestream.TryIsBusy(out var busy) && busy;
        this.trip.OnRequested(accepted, now);

        if (accepted)
        {
            this.acceptedAt = now;
            this.log.Write("検証", $"{this.current.Name} へ向かうよう Lifestream に頼みました");
        }
        else
        {
            this.log.Write("検証", "Lifestream が宿屋への移動を受け付けませんでした。少し置いて頼み直します");
        }
    }

    /// <summary>着いたあと。呼び鈴を確かめるなら開き、確かめないならここで成功として残す。</summary>
    private void AfterArrival()
    {
        if (!this.openBell)
        {
            this.Record(Outcome.Success, "宿屋に入れました（呼び鈴は開いていません）");
            this.Next();
            return;
        }

        // 他のワールドではリテイナーを呼べない（公式の制限）。移動だけを成功として残す。
        if (RetainerAccess.Current() == RetainerAccess.State.Visiting)
        {
            this.Record(Outcome.Success, "宿屋に入れました（他のワールドに居るため、呼び鈴は開いていません）");
            this.Next();
            return;
        }

        this.bell.Reset();
        this.bell.Begin();
        this.stage = Stage.Bell;
    }

    private void TickBell()
    {
        this.bell.Tick();
        this.Detail = $"{this.current.Name}: {this.bell.Detail}";

        switch (this.bell.Current)
        {
            case BellRunner.Step.Done:
                this.Record(Outcome.Success, $"宿屋に入り、呼び鈴を開いて閉じました（{this.bell.Detail}）");
                this.bell.Reset();
                this.Next();
                break;

            case BellRunner.Step.Failed:
                this.Record(Outcome.Failed, $"宿屋には入れましたが、呼び鈴で失敗しました（{this.bell.Detail}）");
                this.bell.Reset();
                this.Next();
                break;
        }
    }

    private void Record(Outcome outcome, string detail)
    {
        var result = new Result(
            this.current.InnTerritoryId,
            this.current.Name,
            DateTime.Now,
            outcome,
            detail,
            outcome == Outcome.Skipped ? null : this.travel,
            outcome == Outcome.Skipped ? null : this.settleGap);

        this.results[this.current.InnTerritoryId] = result;

        var label = outcome switch
        {
            Outcome.Success => "成功",
            Outcome.Failed => "失敗",
            _ => "試していない",
        };

        this.log.Write("検証", $"{this.current.Name}: {label} … {detail}");
    }

    /// <summary>エリア番号と名前。失敗したときに、どこで止まったかを残すため。</summary>
    private static string DescribeTerritory(uint territory)
    {
        try
        {
            var name = Svc.Data.GetExcelSheet<TerritoryType>().TryGetRow(territory, out var row)
                ? row.PlaceName.ValueNullable?.Name.ExtractText()
                : null;

            return string.IsNullOrEmpty(name) ? $"エリア {territory}" : $"エリア {territory} {name}";
        }
        catch (Exception)
        {
            return $"エリア {territory}";
        }
    }

    /// <summary>
    /// アンロードのとき。自分が出した移動（呼び鈴へ歩く）だけを止める。
    /// Lifestream には触らない（こちらを更新・再読み込みしただけで相手の作業が消えないように）。
    /// </summary>
    public void Dispose()
    {
        if (this.Running)
            this.bell.Cancel("プラグインを読み直したため");
    }
}
