using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GBRHelper.Ipc;
using Lumina.Excel.Sheets;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace GBRHelper.Features;

/// <summary>
/// 呼び鈴を探して、歩いて、話しかけ、回収が終わったら画面を閉じる。
///
/// 要になっている考え方:
///   見えている ≠ 話しかけられる。検出範囲と対話範囲は別物。
///
/// 【呼び鈴の座標は覚えない】（0.2.2.0 で削除）
///   以前は街の呼び鈴へ向かっていたため、遠くて見えない呼び鈴の座標を覚えておき、
///   見えなくてもそこへ歩いていた（BellScanner・BellLocationStore）。
///   いまの帰還先は宿屋の部屋で、呼び鈴は扉から 3.5〜5.7m の所にあり、着けば見える。
///   見えるまで少し待つだけにした。
///
/// コメントに残っている「実測で起きたこと」は、実際に踏んだ罠。
/// </summary>
public sealed class BellRunner
{
    /// <summary>話しかけられる距離。これより遠いと「距離が離れています」になる。</summary>
    private const float InteractRange = 3.5f;

    /// <summary>移動の目標。対話範囲より内側に取る（到着判定の揺れで届かないのを防ぐ）。</summary>
    private const float MoveRange = InteractRange - 1f;

    /// <summary>
    /// 歩いている途中で行き先を変えるのに必要な差（m）。
    /// これ未満の差では変えない。ほぼ同距離の呼び鈴が2つあるときに
    /// 行き先が振動して、いつまでも到着しなくなるのを防ぐ。
    /// </summary>
    private const float RetargetMargin = 5f;

    /// <summary>呼び鈴の名前を引く EObjName の行番号。番号は言語に依らない。</summary>
    private const uint BellEObjNameRow = 2000401;

    public enum Step
    {
        Idle,
        Moving,
        Interacting,

        /// <summary>AutoRetainer がベンチャーを回収し終わるのを待つ。</summary>
        Working,

        /// <summary>リテイナーの画面を閉じる。</summary>
        Closing,

        Done,
        Failed,
    }

    private readonly VnavmeshIpc navmesh;
    private readonly RunLog log;
    private readonly AutoRetainerIpc retainer;

    /// <summary>自分が移動を出したか。無条件に止めると他プラグインの移動まで止めてしまう。</summary>
    private bool moveIssued;

    /// <summary>いま向かっている呼び鈴の座標。より近いものが現れたか判断するのに使う。</summary>
    private Vector3? movingTo;

    /// <summary>この回の回収を始めた時刻。呼び鈴を探す猶予を測るのに使う。</summary>
    private DateTime beganAt = DateTime.MinValue;

    /// <summary>
    /// ナビメッシュの読み込みを頼んだか。
    /// 毎フレーム頼むと読み込みが始まり直して、いつまでも終わらない。
    /// </summary>
    private bool reloadRequested;

    /// <summary>
    /// 呼び鈴が見つからないまま探し続ける時間。
    ///
    /// エリアに着いた直後はオブジェクトの一覧が埋まっておらず、
    /// 目の前の呼び鈴も見えない。即座に失敗させると
    /// 「着いた瞬間に必ず失敗する」ことになる（街へ帰っていた頃に実機で踏んだ）。
    ///
    /// 歩き回って読み込ませる仕組みは持たないので、
    /// その場で読み込まれるのを待つ長さとして 45 秒を取る。
    /// </summary>
    private static readonly TimeSpan SearchGrace = TimeSpan.FromSeconds(45);

    /// <summary>
    /// ナビメッシュの読み込みを頼んでから、始まるのを待つ時間。
    ///
    /// Reload は非同期で、頼んだ直後はまだ BuildProgress が負のまま。
    /// ここを待たないと、頼んだ次のフレームで「構築していない」と判断して
    /// 失敗にしてしまう。
    ///
    /// 始まってしまえば BuildProgress が正になり、そちらの経路で待つ。
    /// </summary>
    private static readonly TimeSpan ReloadGrace = TimeSpan.FromSeconds(30);

    private DateTime stepDeadline = DateTime.MinValue;
    private DateTime nextInteract = DateTime.MinValue;
    private DateTime overallDeadline = DateTime.MinValue;

    public Step Current { get; private set; } = Step.Idle;

    public string Detail { get; private set; } = string.Empty;
    public BellCompletion Completion { get; private set; }
    private string completionDetail = "回収の完了を確認できませんでした";

    private void CloseAfter(BellCompletion completion, string detail)
    {
        Completion = completion;
        completionDetail = detail;
        MoveToStep(Step.Closing, "リテイナーの画面を閉じています", 30);
    }

    public bool Running
        => this.Current is Step.Moving or Step.Interacting or Step.Working or Step.Closing;

    public BellRunner(VnavmeshIpc navmesh, RunLog log, AutoRetainerIpc retainer)
    {
        this.navmesh = navmesh;
        this.log = log;
        this.retainer = retainer;
    }

    /// <summary>
    /// いま vnavmesh で歩けるか。歩けないときは理由を返す。
    ///
    /// 【エリア移動の直後は「まだ」使えない】
    /// Nav.IsReady の中身は navmeshManager.Navmesh != null で、
    /// エリアを移った直後は読み込み中のため null になる。
    /// テレポートで着いた瞬間にここを見ると必ず false が返り、
    /// 「vnavmesh が使えない」と判断して即座に失敗していた。
    ///
    /// 呼び出し側は、false でも <paramref name="worthWaiting"/> が true なら待つこと。
    /// 「まだ使えない」と「待っても無駄」は別物。
    /// </summary>
    /// <param name="reason">使えない理由。画面に出す。</param>
    /// <param name="worthWaiting">
    /// 待てば使えるようになる見込みがあるか。
    /// 構築中のときと、こちらから読み込みを頼んだ直後が該当する。
    /// </param>
    private bool NavmeshUsable(out string reason, out bool worthWaiting)
    {
        worthWaiting = false;

        if (!this.navmesh.IsLoaded)
        {
            reason = "vnavmesh が導入されていません";
            return false;
        }

        if (this.navmesh.IsReady())
        {
            reason = string.Empty;
            return true;
        }

        if (this.navmesh.BuildProgress() is { } progress && progress >= 0)
        {
            reason = $"ナビメッシュを構築しています（{progress * 100:F0}%）";
            worthWaiting = true;
            return false;
        }

        // 構築すら始まっていない。
        //
        // vnavmesh の「自動で読み込む」が OFF だと、エリアを移っても
        // 何も読み込まれない（NavmeshManager.cs:68-71 で即 return）。
        // この状態で待ち続けても永久に使えるようにならないので、
        // こちらから一度だけ読み込みを頼む。
        //
        // 相手の設定は書き換えない。設定を変えるのは利用者の領分で、
        // こちらの用が済んだあとも影響が残ってしまう。
        if (!this.reloadRequested)
        {
            this.reloadRequested = true;

            if (this.navmesh.Reload())
            {
                this.log.Write("Bell", "ナビメッシュの読み込みを頼みました");
                reason = "ナビメッシュの読み込みを待っています";
                worthWaiting = true;
                return false;
            }

            this.log.Write("Bell", "ナビメッシュの読み込みを頼めませんでした");
        }
        else
        {
            // 頼んだあと。始まるまで少し掛かるので、しばらくは待つ。
            // ここを待たないと、頼んだ直後の1フレームで失敗にしてしまう。
            if (DateTime.UtcNow - this.beganAt < ReloadGrace)
            {
                reason = "ナビメッシュの読み込みを待っています";
                worthWaiting = true;
                return false;
            }
        }

        reason = this.navmesh.IsAutoLoad() == false
            ? "vnavmesh の「Auto load mesh」が切れているため、"
              + "ナビメッシュが読み込まれません"
            : "vnavmesh のナビメッシュが読み込まれていません";

        return false;
    }

    public void Begin()
    {
        if (this.Running)
            return;

        this.Completion = BellCompletion.Unconfirmed;
        this.completionDetail = "回収の完了を確認できませんでした";
        this.moveIssued = false;
        this.movingTo = null;
        this.retainerStarted = false;
        this.idleSince = null;
        this.beganAt = DateTime.UtcNow;
        this.reloadRequested = false;
        this.overallDeadline = DateTime.UtcNow.AddMinutes(8);
        this.MoveToStep(Step.Moving, "呼び鈴へ向かっています", 90);
    }

    public void Cancel(string reason)
    {
        if (!this.Running)
            return;

        this.StopMoving();
        this.Current = Step.Idle;
        this.Detail = reason;
        this.log.Write("Bell", $"中止しました（{reason}）");
    }

    /// <summary>状態を初期に戻す。次の回収に備えるときに呼ぶ。</summary>
    public void Reset()
    {
        retainerStarted = false; moveIssued = false; movingTo = null; idleSince = null;
        this.Completion = BellCompletion.Unconfirmed;
        this.Current = Step.Idle;
        this.Detail = string.Empty;
    }

    /// <summary>毎フレーム呼ぶ。</summary>
    public void Tick()
    {
        if (!this.Running)
            return;

        if (DateTime.UtcNow > this.overallDeadline)
        {
            this.Fail("全体の制限時間を超えました");
            return;
        }

        switch (this.Current)
        {
            case Step.Moving:
                this.TickMove();
                break;

            case Step.Interacting:
                this.TickInteract();
                break;

            case Step.Working:
                this.TickWorking();
                break;

            case Step.Closing:
                this.TickClosing();
                break;
        }
    }

    // ------------------------------------------------------------------

    private void TickMove()
    {
        // 毎フレーム探し直す。歩いているうちに、より近い呼び鈴が読み込まれることがある。
        var bell = FindBell();

        if (bell is null)
        {
            this.WaitForBell();
            return;
        }

        var distance = Vector3.Distance(bell.Position, Me.Position);

        // もう届く。歩かない。
        if (distance <= InteractRange)
        {
            this.StopMoving();
            this.MoveToStep(Step.Interacting, "呼び鈴に話しかけています", 30);
            return;
        }

        if (!this.NavmeshUsable(out var navReason, out var navWait))
        {
            // 待てば使えるようになるなら待つ。
            // ここで諦めると、待てば歩けたはずの場面で失敗する。
            if (navWait)
            {
                this.Detail = $"{navReason}（呼び鈴まで {distance:F1}m）";
                this.PostponeStep();
                return;
            }

            this.Fail($"呼び鈴まで {distance:F1}m ありますが、{navReason}");
            return;
        }

        // 経路は1度だけ頼む。毎フレーム頼むと依頼が積み上がって動かなくなる。
        if (!this.moveIssued)
        {
            if (!this.navmesh.MoveCloseTo(bell.Position, MoveRange))
            {
                this.Fail("呼び鈴へ向かえません（経路を引けませんでした）");
                return;
            }

            this.moveIssued = true;
            this.movingTo = bell.Position;
            this.log.Write("Bell", $"呼び鈴へ向かいます（あと {distance:F1}m）");
            return;
        }

        // 歩いている途中で、今向かっている先より明らかに近い呼び鈴が
        // 読み込まれることがある。その場合は行き先を変える。
        //
        // 「明らかに」の幅を持たせているのは、ほぼ同じ距離の呼び鈴が2つあるときに
        // 行き先が行ったり来たりして、いつまでも着かなくなるのを防ぐため。
        if (this.movingTo is { } target
            && Vector3.Distance(bell.Position, target) > 0.5f
            && Vector3.Distance(target, Me.Position) - distance > RetargetMargin)
        {
            if (this.navmesh.MoveCloseTo(bell.Position, MoveRange))
            {
                this.movingTo = bell.Position;
                this.log.Write("Bell", $"より近い呼び鈴に行き先を変えます（あと {distance:F1}m）");
            }

            return;
        }

        // 到着の報告は信じない。距離で判断する。
        if (!this.navmesh.IsMoving())
        {
            this.StopMoving();
            this.MoveToStep(Step.Interacting, "呼び鈴に話しかけています", 30);
            return;
        }

        if (this.Expired())
            this.Fail($"呼び鈴まで行けませんでした（あと {distance:F1}m）");
    }

    /// <summary>
    /// 呼び鈴がまだ見えない。着いた直後は一覧が埋まっていないことがあるので、しばらく待つ。
    /// 待っても見えなければ、近くにある物を並べて失敗にする（原因の切り分け用）。
    /// </summary>
    private void WaitForBell()
    {
        var searched = DateTime.UtcNow - this.beganAt;

        if (searched < SearchGrace)
        {
            this.Detail = $"呼び鈴を探しています（{searched.TotalSeconds:F0} 秒）";
            this.PostponeStep();
            return;
        }

        this.Fail($"近くに呼び鈴がありません（{DescribeNearby()}）");
    }

    private unsafe void TickInteract()
    {
        // 成功は状態の変化で確かめる。撃った回数では数えない。
        if (Svc.Condition[ConditionFlag.OccupiedSummoningBell])
        {
            this.log.Write("Bell", "呼び鈴を開きました");
            this.MoveToStep(Step.Working, "リテイナーの処理を待っています", 180);
            this.workingSince = DateTime.UtcNow;
            return;
        }

        if (this.Expired())
        {
            this.Fail("呼び鈴に話しかけられませんでした");
            return;
        }

        // 撃つ間隔を空ける。毎フレーム撃つと詰まって何も起きない。
        if (DateTime.UtcNow < this.nextInteract)
            return;

        this.nextInteract = DateTime.UtcNow.AddSeconds(2);

        var bell = FindBell();

        if (bell is null)
        {
            this.Fail("近くに呼び鈴がありません");
            return;
        }

        // 撃つ直前にもう一度測る。届いていなければ移動へ戻す（失敗にしない）。
        var distance = Vector3.Distance(bell.Position, Me.Position);

        if (distance > InteractRange)
        {
            this.log.Write("Bell", $"呼び鈴から {distance:F1}m 離れています。近寄り直します");
            this.MoveToStep(Step.Moving, "呼び鈴へ向かっています", 90);
            return;
        }

        this.log.Write("Bell", $"呼び鈴に話しかけます（{bell.Name}・{distance:F1}m）");

        // 先に Target を入れる。入れずに撃つと通らないことがある。
        Svc.Targets.Target = bell;
        TargetSystem.Instance()->InteractWithObject((GameObjectStruct*)bell.Address, false);
    }

    /// <summary>AutoRetainer の処理が始まったか。始まる前に「終わった」と誤判定しないため。</summary>
    private bool retainerStarted;

    private DateTime workingSince = DateTime.MinValue;

    /// <summary>
    /// AutoRetainer がベンチャーを回収し終わるのを待つ。
    ///
    /// 「忙しくない」だけで終わったと判断しない。
    /// 開いた直後はまだ動き出しておらず、その瞬間は忙しくないため。
    /// 一度でも忙しくなったことを確かめてから、忙しくなくなるのを待つ。
    /// </summary>
    public void CompleteClosedSession(AutoRetainerIpc.VentureState state)
    {
        Completion = state == AutoRetainerIpc.VentureState.None
            ? (retainerStarted ? BellCompletion.Collected : BellCompletion.NothingToCollect)
            : retainerStarted && state == AutoRetainerIpc.VentureState.Unknown
                ? BellCompletion.Unconfirmed : BellCompletion.Failed;
        completionDetail = Completion switch
        {
            BellCompletion.Collected => "ベンチャーを回収し、画面が閉じられました",
            BellCompletion.NothingToCollect => "回収対象はありませんでした",
            BellCompletion.Unconfirmed => "画面が閉じられましたが、回収結果を確認できません",
            _ => "画面が閉じられましたが、回収が完了していません",
        };
        Finish(completionDetail);
    }

    public void ObserveNotStarted(AutoRetainerIpc.VentureState state, DateTime now)
    {
        if (state == AutoRetainerIpc.VentureState.None)
        {
            CloseAfter(BellCompletion.NothingToCollect, "回収対象はありませんでした");
            return;
        }
        if (now - workingSince < StartGrace) return;
        this.CloseAfter(BellCompletion.Failed, "AutoRetainer が回収を開始しませんでした");
    }

    private void TickWorking()
    {
        // 【重要】OccupiedSummoningBell だけで判定しない。
        // リテイナー一覧が開いている間、このフラグは落ちていることがある。
        // フラグだけを見ていると「閉じられた」と誤判定し、
        // 実際には画面が開いたままなのに完了扱いになってしまう（実際にそうなっていた）。
        // 画面が出ているかどうかも合わせて見る。
        if (!IsBellSessionOpen())
        {
            CompleteClosedSession(this.retainer.CheckCollectableVenture());
            return;
        }

        if (this.Expired())
        {
            this.log.Write("Bell", "リテイナーの処理が長引いています。閉じます");
            this.CloseAfter(BellCompletion.Failed, "リテイナーの処理が時間切れになりました");
            return;
        }

        if (!this.retainer.IsLoaded)
        {
            // AutoRetainer が無ければ待っても始まらない。開いたまま閉じる。
            this.log.Write("Bell", "AutoRetainer が無いため、開いただけで閉じます");
            this.CloseAfter(BellCompletion.Failed, "AutoRetainer が読み込まれていません");
            return;
        }

        if (!this.retainer.TryIsBusy(out var busy))
            return;

        if (busy)
        {
            this.retainerStarted = true;
            this.idleSince = null;
            this.Detail = "ベンチャーを回収しています";
            return;
        }

        // 動き出す前の「忙しくない」は無視する。
        if (!this.retainerStarted)
        {
            ObserveNotStarted(this.retainer.CheckCollectableVenture(), DateTime.UtcNow);
            return;
        }

        // リテイナー1人ずつ処理する間、一瞬だけ忙しくなくなる瞬間がある。
        // そこで閉じると途中で打ち切ってしまうので、落ち着いてから閉じる。
        this.idleSince ??= DateTime.UtcNow;

        // まだ回収できるものが残っていれば、閉じずに待つ。
        //
        // ここでは「分からない」も閉じない側に倒す。
        // 判断できないことを「もう無い」と解釈すると、
        // 読めなかった一瞬のせいで回収を途中で打ち切ってしまう。
        var state = this.retainer.CheckCollectableVenture();

        if (state != AutoRetainerIpc.VentureState.None)
        {
            this.idleSince = null;
            this.Detail = state == AutoRetainerIpc.VentureState.Collectable
                ? "まだ回収できるベンチャーがあります"
                : "リテイナーの状態を確認しています";
            return;
        }

        if (DateTime.UtcNow - this.idleSince.Value < IdleBeforeClose)
        {
            this.Detail = "回収が終わったか確認しています";
            return;
        }

        this.log.Write("Bell", "回収できるベンチャーが無くなりました。画面を閉じます");
        this.CloseAfter(BellCompletion.Collected, "ベンチャーを回収しました");
    }

    /// <summary>
    /// 「忙しくない」がこの時間続いたら、本当に終わったとみなす。
    ///
    /// AutoDuty は同じ判断に 2 秒を使っている
    /// （AutoRetainerHelper: IsBusy が false になったら 2000ms スロットル）。
    /// こちらは少し余裕を持たせて 3 秒。
    /// 回収できるベンチャーが残っていないかも毎回確かめているので、
    /// 短くても途中で打ち切る心配はない。
    /// </summary>
    private static readonly TimeSpan IdleBeforeClose = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 「回収できるはずなのに AutoRetainer が動き出さない」ときに待つ上限。
    /// 回収するものが無い場合はこれを待たずに即座に閉じる（直接聞けるため）。
    /// </summary>
    private static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(10);

    /// <summary>忙しくなくなった時刻。</summary>
    private DateTime? idleSince;

    /// <summary>
    /// リテイナーの画面を閉じる。
    /// 開いたままだと採集へ戻れないため、必ず閉じきってから次へ進む。
    /// </summary>
    private void TickClosing()
    {
        // 閉じたことは、フラグと画面の両方が消えたかで確かめる。
        if (!IsBellSessionOpen())
        {
            this.Finish("リテイナーの画面を閉じました");
            return;
        }

        if (this.Expired())
        {
            this.Fail("リテイナーの画面を閉じられませんでした");
            return;
        }

        if (DateTime.UtcNow < this.nextInteract)
            return;

        this.nextInteract = DateTime.UtcNow.AddSeconds(2);

        // AutoRetainer が動いていると閉じる操作と取り合う。先に止める。
        if (this.retainer.IsLoaded && this.retainer.TryIsBusy(out var busy) && busy)
        {
            this.retainer.TryAbort();
            return;
        }

        // 開いている画面を順に閉じる。
        CloseRetainerWindows();
    }

    /// <summary>
    /// 呼び鈴を使っている最中か。
    ///
    /// ConditionFlag.OccupiedSummoningBell はリテイナー一覧を開いている間に
    /// 落ちることがあるため、これだけでは判定できない。
    /// 関連する画面が出ていれば「まだ使用中」とみなす。
    /// </summary>
    public static unsafe bool IsBellSessionOpen()
    {
        if (Svc.Condition[ConditionFlag.OccupiedSummoningBell])
            return true;

        foreach (var name in RetainerAddons)
        {
            var addon = (AtkUnitBase*)(nint)Svc.GameGui.GetAddonByName(name);

            if (addon != null && addon->IsVisible)
                return true;
        }

        return false;
    }

    /// <summary>リテイナー関連の画面。AutoDuty が閉じ対象にしているものと同じ。</summary>
    private static readonly string[] RetainerAddons =
        ["RetainerList", "SelectString", "SelectYesno", "RetainerTaskAsk", "RetainerTaskResult"];

    /// <summary>リテイナー関連の画面を閉じる。</summary>
    private static unsafe void CloseRetainerWindows()
    {
        foreach (var name in RetainerAddons)
        {
            var addon = (AtkUnitBase*)(nint)Svc.GameGui.GetAddonByName(name);

            if (addon != null && addon->IsVisible)
                addon->Close(true);
        }
    }

    private void Finish(string detail)
    {
        this.StopMoving();
        // 閉じただけでは成功にしない。未開始・時間切れなどの結果を Closing 越しに保持する。
        if (Completion is BellCompletion.Unconfirmed or BellCompletion.Failed)
        {
            Completion = BellCompletion.Failed;
            this.Current = Step.Failed;
            this.Detail = completionDetail;
        }
        else
        {
            this.Current = Step.Done;
            this.Detail = completionDetail;
        }
        this.retainerStarted = false;
        this.log.Write("Bell", detail);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// 現在地から一番近い呼び鈴を探す。距離で切らない（読み込まれていれば全部拾う）。
    /// EventObj だけでなく HousingEventObject も見る。家の中で見つからなくなるため。
    ///
    /// 正式名と完全に一致するものを優先する。
    /// 同じ「呼び鈴」でも、名前に別の語が付いた紛らわしいものがあるため、
    /// 確実なものが見つかっているなら、多少遠くてもそちらを選ぶ。
    /// </summary>
    public static IGameObject? FindBell()
    {
        var bellName = Svc.Data.GetExcelSheet<EObjName>()?
            .GetRowOrDefault(BellEObjNameRow)?.Singular.ExtractText() ?? string.Empty;

        IGameObject? nearest = null;
        var nearestDistance = float.MaxValue;
        var nearestIsExact = false;
        var me = Me.Position;

        foreach (var obj in Svc.Objects)
        {
            if (obj.ObjectKind is not (ObjectKind.EventObj or ObjectKind.HousingEventObject))
                continue;

            var name = obj.Name.ToString();

            // 「美容師の呼び鈴」など、名前に「呼び鈴」を含むが
            // リテイナーとは無関係なものがある。話しかけても一覧が開かない。
            if (IsDecoyBell(name))
                continue;

            var exact = !string.IsNullOrEmpty(bellName)
                        && string.Equals(name, bellName, StringComparison.Ordinal);

            var matches = exact
                          || (!string.IsNullOrEmpty(bellName) && name.Contains(bellName, StringComparison.Ordinal))
                          || name.Contains("呼び鈴", StringComparison.Ordinal)
                          || name.Contains("Summoning Bell", StringComparison.OrdinalIgnoreCase);

            if (!matches || !obj.IsTargetable)
                continue;

            var distance = Vector3.Distance(obj.Position, me);

            // 正式名が一致するものを優先。
            // 同じ種類どうしなら、近いほうを選ぶ。
            var better = nearest is null
                         || (exact && !nearestIsExact)
                         || (exact == nearestIsExact && distance < nearestDistance);

            if (!better)
                continue;

            nearest = obj;
            nearestDistance = distance;
            nearestIsExact = exact;
        }

        return nearest;
    }

    /// <summary>
    /// 名前に「呼び鈴」を含むが、リテイナーとは関係ないもの。
    ///
    /// 実測で「美容師の呼び鈴」を掴んでしまい、話しかけても一覧が開かず
    /// 2秒おきに話しかけ続ける状態になった。名前で除外する。
    /// </summary>
    private static bool IsDecoyBell(string name)
        => name.Contains("美容師", StringComparison.Ordinal)
           || name.Contains("Aesthetician", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Crystal Bell", StringComparison.OrdinalIgnoreCase);

    /// <summary>見つからなかったときに何が近くにあるかを残す。原因の切り分け用。</summary>
    private static string DescribeNearby()
    {
        var me = Me.Position;

        var near = Svc.Objects
            .Where(x => x.ObjectKind is ObjectKind.EventObj or ObjectKind.HousingEventObject)
            .Select(x => (Name: x.Name.ToString(), Distance: Vector3.Distance(x.Position, me)))
            .Where(x => x.Distance <= 15f)
            .OrderBy(x => x.Distance)
            .Take(4)
            .Select(x => $"{x.Name} {x.Distance:F1}")
            .ToList();

        return near.Count == 0
            ? "近くに触れるものがありません"
            : $"近くにあるもの: {string.Join(" / ", near)}";
    }

    // ------------------------------------------------------------------

    private void MoveToStep(Step step, string detail, int seconds)
    {
        this.Current = step;
        this.Detail = detail;
        this.stepDeadline = DateTime.UtcNow.AddSeconds(seconds);
    }

    private bool Expired() => DateTime.UtcNow > this.stepDeadline;

    /// <summary>
    /// いまの段階の締め切りを先へ延ばす。
    ///
    /// 「こちらの都合ではなく、相手の準備が終わるのを待っている」間に使う。
    /// ナビメッシュの構築は環境によって時間が読めないため、
    /// 待っている時間を段階の制限時間に数えると、
    /// 構築が終わる前に時間切れで失敗してしまう。
    ///
    /// 待ち続けても全体の制限時間（Begin で入れる8分）は進むので、
    /// いつまでも終わらない場合はそちらで止まる。
    /// </summary>
    private void PostponeStep()
        => this.stepDeadline = DateTime.UtcNow.AddSeconds(30);

    private void Fail(string reason)
    {
        this.StopMoving();
        this.Current = Step.Failed;
        this.Detail = reason;
        this.log.Write("Bell", reason);
    }

    /// <summary>自分が始めた移動だけを止める。</summary>
    private void StopMoving()
    {
        if (!this.moveIssued)
            return;

        this.moveIssued = false;
        this.navmesh.Stop();
    }
}
