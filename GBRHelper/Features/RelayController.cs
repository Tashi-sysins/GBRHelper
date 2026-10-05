using System;
using Dalamud.Game.ClientState.Conditions;
using GBRHelper.Ipc;

namespace GBRHelper.Features;

/// <summary>
/// 全体の流れを取り仕切る。
///
/// ベンチャー回収可能を検知
///   → 採集ノードの切れ目まで待つ
///   → GBR の自動採集を OFF
///   → 帰還先の宿屋の部屋へ入る（Lifestream の宿屋機能。判断は InnTrip）
///   → 部屋の呼び鈴へ移動・アクセス・回収・画面を閉じる（BellRunner）
///   → GBR の自動採集を ON に戻す
///
/// 【帰還先を街から宿屋へ変えた理由】
///   宿屋の部屋には 8 都市とも呼び鈴があり（配置ファイルで確認）、
///   扉から 3.5〜5.7m の位置にある。街の呼び鈴より探す手間が少なく、人も居ない。
///   宿屋までの移動（テレポート・都市内転送・徒歩・受付との会話）は Lifestream に任せる。
///   宿屋までの経路と受付は Lifestream 自身が持っているので、自前では作らない。
///
/// 【設計の要点】
///
/// 1. GBR と完全に連動する。
///    GBR の自動採集が ON になればこちらも動き出し、OFF になれば止まる。
///    判断は GBR からのイベント（AutoGatherEnabledChanged）で受ける。
///    毎フレームの読み取りで実装すると、こちらが復帰のために ON へ戻した瞬間を
///    「利用者が操作した」と取り違える。
///
/// 2. 自分が操作したぶんは無視する。
///    回収後に GBR を ON へ戻すと、その通知が返ってくる。
///    これをそのまま受けると「利用者が ON にした」と読んで
///    回収をもう一度始めてしまう。自分で出した変更には印を付けて読み飛ばす。
///
/// 3. 採集ノードの途中では抜けない。
///    ConditionFlag.Gathering が立っている間はノードを開いている。
///    ここで止めると採り残しが出る。落ちるのを待ってから中断する。
/// </summary>
public sealed class RelayController : IDisposable
{
    /// <summary>いま何をしているか。</summary>
    public enum Phase
    {
        /// <summary>このプラグインは動いていない。</summary>
        Off,

        /// <summary>動いているが、回収の用事は無い。ベンチャーの状態を見ている。</summary>
        Watching,

        /// <summary>回収したいが、採集ノードの切れ目を待っている。</summary>
        WaitingForBreak,

        /// <summary>帰還先の宿屋へ向かっている。</summary>
        Returning,

        /// <summary>呼び鈴で回収している。</summary>
        Collecting,

        /// <summary>自動採集へ戻している。</summary>
        Resuming,
    }

    private readonly Configuration config;
    private readonly RunLog log;
    private readonly GatherBuddyIpc gatherBuddy;
    private readonly AutoRetainerIpc retainer;
    private readonly LifestreamIpc lifestream;
    private readonly VnavmeshIpc navmesh;
    private readonly BellRunner bell;
    private readonly InnService inns;

    /// <summary>宿屋へ向かう段の判断。</summary>
    private readonly InnTrip innTrip = new();

    public Phase Current { get; private set; } = Phase.Off;

    /// <summary>画面に出す一行説明。分岐には使わない。</summary>
    public string Detail { get; private set; } = "待機中";

    /// <summary>このプラグインが動いているか。</summary>
    public Func<bool> CollectionBlocked { get; set; } = () => false;
    private bool forceCollectionPending;

    /// <summary>精選中の要求は Watching のまま予約し、終了後に一度だけ実行する。</summary>
    public bool TryStartQueuedCollection()
    {
        if (!forceCollectionPending || !Active || CollectionBlocked()) return false;
        forceCollectionPending = false;
        ForceCollectNow();
        return true;
    }

    public bool Active => this.Current != Phase.Off;

    /// <summary>これまでに回収した回数。</summary>
    public int CollectedCount { get; private set; }

    /// <summary>直近の回収結果。</summary>
    public string LastResult { get; private set; } = string.Empty;

    public RelayController(
        Configuration config,
        RunLog log,
        GatherBuddyIpc gatherBuddy,
        AutoRetainerIpc retainer,
        LifestreamIpc lifestream,
        VnavmeshIpc navmesh,
        BellRunner bell,
        InnService inns)
    {
        this.config = config;
        this.log = log;
        this.gatherBuddy = gatherBuddy;
        this.retainer = retainer;
        this.lifestream = lifestream;
        this.navmesh = navmesh;
        this.bell = bell;
        this.inns = inns;

        this.gatherBuddy.EnabledChanged += this.OnGatherBuddyEnabledChanged;
    }

    public void Dispose()
    {
        this.gatherBuddy.EnabledChanged -= this.OnGatherBuddyEnabledChanged;

        // 【アンロード経路では相手に触れない】
        // ここで GBR を操作すると、こちらを更新・再読み込みしただけで
        // 相手の採集が止まる。自分が出した移動だけを片付けて終わる。
        if (this.ownsMovement)
        {
            this.navmesh.Stop();
            this.ownsMovement = false;
        }
    }

    // ------------------------------------------------------------------
    // GBR との連動

    // GBRのIPC通知はSetの呼び出し内で同期的に届く。拒否された呼び出しの分を後へ持ち越さない。
    private int selfIssuedChanges;
    public Func<bool> CharacterReady { get; set; } = () => true;

    public void ResetCharacter()
    {
        Current = Phase.Off; forceCollectionPending = false; ownsMovement = false;
        bell.Reset(); innTrip.Reset(); requestedInn = null; breakWaitStarted = null;
        nextAttemptAllowedAt = nextVentureCheck = resumeStartedAt = DateTime.MinValue;
        consecutiveFailures = 0; CollectedCount = 0; LastResult = ""; Detail = "待機中";
        defaultPending = true; resumeIssued = false; selfIssuedChanges = 0; CooldownReason = "";
    }

    private void OnGatherBuddyEnabledChanged(bool enabled)
    {
        if (!CharacterReady()) return;
        // 自分が出した変更なら読み飛ばす。
        if (this.selfIssuedChanges > 0)
        {
            this.log.Write("GBR", $"自動採集が {(enabled ? "ON" : "OFF")} になりました（このプラグインの操作）");
            return;
        }

        if (enabled)
        {
            // 「有効」のチェックが OFF なら見張らない。
            if (!this.config.VentureRelayOn)
            {
                this.log.Write("GBR", "自動採集が ON になりました（ベンチャー回収は無効なので見張りません）");
                return;
            }

            this.log.Write("GBR", "自動採集が ON になりました。ベンチャーの監視を始めます");
            this.Start();
        }
        else
        {
            this.log.Write("GBR", "自動採集が OFF になりました。ベンチャーの監視を止めます");
            this.Stop("GatherBuddyReborn の自動採集が止まりました");
        }
    }

    /// <summary>
    /// GBR の自動採集を切り替える。自分が出した変更として記録する。
    /// </summary>
    /// <returns>切り替えが通ったか。</returns>
    private bool SetGatherBuddy(bool enabled)
    {
        // 既にその値なら何もしない。無駄な通知を出さない。
        if (this.gatherBuddy.IsAutoGatherEnabled() == enabled)
            return true;

        return IssueGatherBuddyChange(() => this.gatherBuddy.SetAutoGatherEnabled(enabled));
    }

    public bool IssueGatherBuddyChange(Func<bool> request)
    {
        this.selfIssuedChanges++;
        try { return request(); }
        finally { this.selfIssuedChanges--; }
    }

    // ------------------------------------------------------------------
    // 開始・停止

    /// <summary>
    /// 「有効」のチェックを切り替えたあとに呼ぶ（2026-10-05）。有効にして GBR が採集中なら見張りを始め、
    /// 無効にして見張っているだけ（回収の途中でない）なら止める。回収の途中では画面のチェックを押せない（MainWindow）。
    /// </summary>
    public void ApplyEnabled(bool gatherBuddyRunning)
    {
        if (this.config.VentureRelayOn && gatherBuddyRunning && !this.Active)
        {
            this.log.Write("GBR", "ベンチャー回収を有効にしました。ベンチャーの監視を始めます");
            this.Start();
        }
        else if (!this.config.VentureRelayOn && this.Current == Phase.Watching)
        {
            this.log.Write("GBR", "ベンチャー回収を無効にしました。ベンチャーの監視を止めます");
            this.Stop("ベンチャー回収を無効にしました");
        }
    }

    /// <summary>監視を始める。</summary>
    public void Start()
    {
        if (this.Active)
            return;

        this.Current = Phase.Watching;
        this.Detail = "ベンチャーの状態を見ています";
        this.bell.Reset();
    }

    /// <summary>監視を止める。途中の作業があれば片付ける。</summary>
    public void Stop(string reason)
    {
        this.forceCollectionPending = false;
        if (!this.Active)
            return;

        // 呼び鈴へ向かっている途中なら止める。自分が出した移動だけを止める。
        if (this.bell.Running)
            this.bell.Cancel(reason);

        if (this.ownsMovement)
        {
            this.navmesh.Stop();
            this.ownsMovement = false;
        }

        // 宿屋へ向かう途中なら Lifestream も止める。
        // 自分が頼んだ移動を受け付けてもらっていたときだけ。他の用事で動いている分は触らない。
        this.AbortOwnInnTrip();

        this.Current = Phase.Off;
        this.Detail = reason;
        this.breakWaitStarted = null;
    }

    /// <summary>
    /// 自分が頼んだ宿屋への移動が Lifestream で走っていれば止める。
    ///
    /// 頼んでいない（受け付けられていない）ときは触らない。
    /// Lifestream の Abort は作業列を丸ごと消すので、利用者や他のプラグインの頼みごとまで消してしまう。
    /// </summary>
    private void AbortOwnInnTrip()
    {
        if (this.innTrip.Requested && this.lifestream.IsLoaded)
        {
            this.lifestream.TryAbort();
            this.log.Write("Relay", "自分が頼んだ宿屋への移動を止めました");
        }

        this.innTrip.Reset();
    }

    // ------------------------------------------------------------------
    // 毎フレームの処理

    /// <summary>自分が移動を出しているか。</summary>
    private bool ownsMovement;

    /// <summary>切れ目を待ち始めた時刻。</summary>
    private DateTime? breakWaitStarted;

    /// <summary>
    /// 今回頼んだ宿屋の部屋のエリア番号。Lifestream に任せたときは null。
    /// 着いた宿屋が指定どおりかを確かめるのに使う。
    /// </summary>
    private uint? requestedInn;

    /// <summary>次に回収を試してよい時刻。失敗のあと間隔を空けるのに使う。</summary>
    private DateTime nextAttemptAllowedAt = DateTime.MinValue;

    /// <summary>
    /// 帰還先の既定をまだ入れていないか。
    ///
    /// 一覧が読めるまで試し続ける必要があるが、
    /// 一度入れたら（あるいは既に設定済みだと分かったら）もう試さない。
    /// </summary>
    private bool defaultPending = true;

    /// <summary>ベンチャーの状態を聞く間隔。毎フレーム聞く必要はない。</summary>
    private DateTime nextVentureCheck = DateTime.MinValue;

    private static readonly TimeSpan VentureCheckInterval = TimeSpan.FromSeconds(5);

    public void Tick()
    {
        // 購読は1回だけ成功すればよい。GBR がこちらより後に起動することがあるので、
        // 成功するまで試し続ける。
        if (!this.gatherBuddy.Subscribed)
            this.gatherBuddy.TrySubscribeEnabledChanged();

        // 帰還先が未設定なら既定（リムサの宿屋）を入れる。
        //
        // ここで毎フレーム試すのは、エーテライトの一覧が
        // 起動直後やコンテンツ内では読めないため。
        // 読めるようになった時点で入る。入ったら以後は素通りする
        // （TryApplyDefault が設定済みかどうかを見て即座に false を返す）。
        if (this.defaultPending)
        {
            if (this.inns.TryApplyDefault(this.config))
            {
                this.log.Write("設定",
                    $"帰還先を既定の {this.config.InnName} にしました");
                this.defaultPending = false;
            }
            else if (this.config.InnAuto || this.config.InnTerritoryId != 0)
            {
                // 既に設定済みだった。試す必要はない。
                this.defaultPending = false;
            }
        }

        if (!this.Active)
            return;

        TickActive(Me.Available, AdvancePhase);
    }

    public void TickActive(bool available, Action advance)
    {
        if (!Active || !available) return;
        this.TryStartQueuedCollection();
        advance();
    }

    private void AdvancePhase()
    {
        switch (this.Current)
        {
            case Phase.Watching:
                this.TickWatching();
                break;

            case Phase.WaitingForBreak:
                this.TickWaitingForBreak();
                break;

            case Phase.Returning:
                this.TickReturning();
                break;

            case Phase.Collecting:
                this.TickCollecting();
                break;

            case Phase.Resuming:
                this.TickResuming();
                break;
        }
    }

    // ------------------------------------------------------------------

    /// <summary>ベンチャーが回収できる状態になるのを待つ。</summary>
    private void TickWatching()
    {
        var now = DateTime.UtcNow;

        if (now < this.nextAttemptAllowedAt)
        {
            var remain = this.nextAttemptAllowedAt - now;
            this.Detail = $"前回の失敗から {remain.TotalMinutes:F0} 分後に再挑戦します";
            return;
        }

        if (now < this.nextVentureCheck)
            return;

        this.nextVentureCheck = now.Add(VentureCheckInterval);

        if (!this.retainer.IsLoaded)
        {
            this.Detail = "AutoRetainer が見つかりません";
            return;
        }

        switch (this.retainer.CheckCollectableVenture())
        {
            case AutoRetainerIpc.VentureState.Collectable:
                // 回収できるものがあっても、いまの場所から向かえないなら見送る（監視は続ける）。
                if (!this.CanStartCollection(out var notNowReason))
                {
                    this.Detail = $"回収できるベンチャーがあります。{notNowReason}";
                    break;
                }

                this.log.Write("Relay", "回収できるベンチャーを見つけました。採集の切れ目を待ちます");
                this.Current = Phase.WaitingForBreak;
                this.breakWaitStarted = DateTime.UtcNow;
                this.Detail = "採集ノードを取り終わるのを待っています";
                break;

            case AutoRetainerIpc.VentureState.None:
                this.Detail = "回収できるベンチャーはありません";
                break;

            default:
                // 読めない。次の機会に聞き直す。「無い」と決めつけない。
                this.Detail = "リテイナーの状態を確認しています";
                break;
        }
    }

    /// <summary>
    /// いま採集ノードを開いているか。
    ///
    /// GatherBuddyReborn 本体の IsGathering と同じ式にしてある
    /// （AutoGather.Var.cs:44-45）。
    ///   Gathering                = ノードを開いている
    ///   ExecutingGatheringAction = 採集アクションの実行中
    ///
    /// 片方だけを見ると、アクションの実行中に「切れ目が来た」と誤判定して
    /// 採り残しが出る。相手と同じ判定にしておけば、ずれない。
    /// </summary>
    internal static bool IsGathering
        => Svc.Condition[ConditionFlag.Gathering]
           || Svc.Condition[ConditionFlag.ExecutingGatheringAction];

    /// <summary>
    /// 採集ノードの切れ目を待つ。
    ///
    /// ノードを開いている間は <see cref="IsGathering"/> が立つ。
    /// 落ちた瞬間が「1つ取り終わった」タイミング。
    /// ここで抜ければ採り残しが出ない。
    /// </summary>
    private void TickWaitingForBreak()
    {
        var waited = this.breakWaitStarted is { } started
            ? DateTime.UtcNow - started
            : TimeSpan.Zero;


        // ノードを開いている最中は抜けない。
        if (IsGathering)
        {
            this.Detail = $"採集中です。取り終わるのを待っています（{waited.TotalMinutes:F0} 分経過）";
            return;
        }

        // 戦闘・詠唱・搭乗の切り替えなど、抜けると危ない状態では待つ。
        // ここは時間切れでも無視しない。
        if (IsUnsafeToBreak(out var unsafeReason))
        {
            this.Detail = $"いまは中断できません（{unsafeReason}）";
            return;
        }

        // 待っている間に他のワールドやコンテンツへ移っていたら、向かわずに監視へ戻る。
        // 「いますぐ回収」のボタンは監視を通らずにここへ来るので、ここでも確かめる。
        if (!this.CanStartCollection(out var notNowReason))
        {
            this.log.Write("Relay", notNowReason);
            this.Current = Phase.Watching;
            this.Detail = notNowReason;
            this.breakWaitStarted = null;
            return;
        }

        this.BeginReturn();
    }

    /// <summary>
    /// いまの場所から回収に向かってよいか。自動採集を止める前に確かめる。
    ///
    /// ・他のワールドに滞在している間はリテイナーを呼べない（公式の制限）。
    ///   向かっても呼び鈴で失敗するだけ。
    /// ・アクセス済みエーテライトの一覧が読めない場所（コンテンツの中）ではテレポートできない。
    ///   ここで自動採集を止めると、止めたまま動けなくなる（以前は上限なしで待っていた）。
    ///
    /// どちらも採集を止めずにそのまま続けてもらい、失敗としては数えない
    /// （環境の都合であって、設定の誤りではない）。場所が変われば次の確認で向かう。
    /// </summary>
    private bool CanStartCollection(out string reason)
    {
        if (RetainerAccess.Current() == RetainerAccess.State.Visiting)
        {
            reason = "他のワールドに滞在中はリテイナーを呼べないため、回収に向かいません"
                     + "（ホームワールドへ戻ると再開します）";
            return false;
        }

        if (!this.inns.IsListReady())
        {
            reason = "いまはテレポートできる場所ではないため、回収に向かいません"
                     + "（コンテンツの中などでエーテライトの一覧が読めません）";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// いま採集を中断すると危ない状態か。
    ///
    /// 時間切れでも無視してはいけないものだけをここに入れる。
    /// 「採集中」は切れ目を待つ理由であって危険ではないので含めない。
    /// </summary>
    internal static bool IsUnsafeToBreak(out string reason)
    {
        if (Svc.Condition[ConditionFlag.InCombat])
        {
            reason = "戦闘中";
            return true;
        }

        if (Svc.Condition[ConditionFlag.Casting])
        {
            reason = "詠唱中";
            return true;
        }

        if (Svc.Condition[ConditionFlag.BetweenAreas]
            || Svc.Condition[ConditionFlag.BetweenAreas51])
        {
            reason = "エリア移動中";
            return true;
        }

        if (Svc.Condition[ConditionFlag.Occupied]
            || Svc.Condition[ConditionFlag.Occupied33]
            || Svc.Condition[ConditionFlag.Occupied38]
            || Svc.Condition[ConditionFlag.Occupied39]
            || Svc.Condition[ConditionFlag.OccupiedInEvent]
            || Svc.Condition[ConditionFlag.OccupiedInQuestEvent])
        {
            reason = "他の操作中";
            return true;
        }

        if (Svc.Condition[ConditionFlag.Unconscious])
        {
            reason = "戦闘不能";
            return true;
        }

        // 釣りは「切れ目」が採集ノードと違う（浮きを出している間が長い）。
        // 途中で抜けると仕掛けを無駄にするので、終わるまで待つ。
        if (Svc.Condition[ConditionFlag.Fishing])
        {
            reason = "釣り中";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    // ------------------------------------------------------------------
    // 帰還

    /// <summary>自動採集を止めて、帰還先の宿屋へ向かう。</summary>
    private void BeginReturn()
    {
        if (!this.SetGatherBuddy(false))
        {
            this.FinishAttempt(false, "自動採集を止められませんでした");
            return;
        }

        this.log.Write("Relay", "自動採集を止めました");

        this.Current = Phase.Returning;
        this.Detail = "帰還先の宿屋へ向かいます";
        this.innTrip.Reset();
        this.requestedInn = null;
    }

    /// <summary>
    /// 帰還先の宿屋の部屋へ入る。
    ///
    /// 判断は InnTrip に任せる。ここでは状態を集めて渡し、返ってきた指示を実行するだけ。
    /// 宿屋の機能は Lifestream の作業列に積まれるので、受け付けたかも終わったかも IsBusy で分かる。
    /// 着いたかはエリアが宿屋の部屋になったかで確かめる（Lifestream が終わったことだけを信じない）。
    /// </summary>
    private void TickReturning()
    {
        var territory = Svc.ClientState.TerritoryType;

        bool? busy = null;

        if (this.lifestream.IsLoaded && this.lifestream.TryIsBusy(out var b))
            busy = b;

        var input = new InnTrip.Input(
            InInn: InnService.IsInInn(territory),
            Transitioning: IsTransitioning(),
            LifestreamLoaded: this.lifestream.IsLoaded,
            LifestreamBusy: busy,
            Gathering: IsGathering,
            UnsafeReason: IsUnsafeToBreak(out var unsafeReason) ? unsafeReason : null,
            DestinationReady: this.inns.IsListReady());

        var decision = this.innTrip.Step(input, DateTime.UtcNow);
        this.Detail = decision.Detail;

        switch (decision.Action)
        {
            case InnTrip.Action.Request:
                this.RequestInn();
                break;

            case InnTrip.Action.Arrived:
                this.OnArrivedAtInn(territory, decision.Detail);
                break;

            case InnTrip.Action.Fail:
                // 自分が頼んだ移動が走っている可能性があるときだけ止める。
                if (decision.AbortLifestream)
                    this.AbortOwnInnTrip();

                this.FinishAttempt(false, decision.Detail);
                break;
        }
    }

    /// <summary>
    /// Lifestream に宿屋への移動を頼み、受け付けられたかを確かめる。
    /// 行き先の確認（設定済みか・一覧にあるか・アクセス済みか）は頼む直前にここで行う。
    /// </summary>
    private void RequestInn()
    {
        int? index;
        string name;

        if (this.config.InnAuto)
        {
            // どの宿屋にするかを Lifestream に任せる。
            index = null;
            name = "Lifestream が選ぶ宿屋";
        }
        else
        {
            if (this.config.InnTerritoryId == 0)
            {
                this.FinishAttempt(false, "帰還先の宿屋が設定されていません");
                return;
            }

            if (this.inns.Find(this.config.InnTerritoryId) is not { } inn)
            {
                this.FinishAttempt(false, string.IsNullOrEmpty(this.inns.BuildError)
                    ? $"設定された宿屋（エリア {this.config.InnTerritoryId}）が宿屋の一覧にありません"
                    : $"宿屋の一覧を作れませんでした（{this.inns.BuildError}）");
                return;
            }

            // 一覧が読めること（コンテンツの中ではない）は InnTrip が確かめてからここへ来る。
            // ここで改めて待つと、上限の無い待ちになる。
            if (!this.inns.IsAttuned(inn))
            {
                this.FinishAttempt(false,
                    $"{inn.Name} のある都市のエーテライトにアクセスしていないため、テレポートできません");
                return;
            }

            index = inn.LifestreamIndex;
            name = inn.Name;
        }

        if (!this.lifestream.TryEnqueueLocalInn(index))
        {
            this.FinishAttempt(false,
                "Lifestream に宿屋への移動を頼めませんでした（通信の失敗は「必要なプラグイン」の画面に出ます）");
            return;
        }

        // 受け付けたかは、直後に IsBusy が立ったかで分かる。
        // 受け付けると同じフレームの中で作業列に積まれる（TaskPropertyShortcut.Enqueue）。
        var accepted = this.lifestream.TryIsBusy(out var busy) && busy;
        this.innTrip.OnRequested(accepted, DateTime.UtcNow);

        if (accepted)
        {
            this.requestedInn = this.config.InnAuto ? null : this.config.InnTerritoryId;
            this.log.Write("Relay", $"{name} へ向かうよう Lifestream に頼みました");
        }
        else
        {
            this.log.Write("Relay", "Lifestream が宿屋への移動を受け付けませんでした。少し置いて頼み直します");
        }
    }

    /// <summary>宿屋の部屋に着いた。呼び鈴へ向かう。</summary>
    private void OnArrivedAtInn(uint territory, string detail)
    {
        // 指定と違う宿屋に着いたら記録に残し、そのまま進める。
        // どの宿屋の部屋にも呼び鈴があるので、回収そのものはできる。
        // 違う宿屋に着くのは、Lifestream の宿屋表とこちらの一覧がずれたとき
        // （パッチで宿屋が増え、どちらか一方だけが追随した等）。
        if (this.requestedInn is { } wanted && wanted != territory)
        {
            this.log.Write("Relay",
                $"指定の宿屋（エリア {wanted}）ではなく、エリア {territory} の宿屋に着きました。"
                + "Lifestream の宿屋の並びが変わった可能性があります。呼び鈴はどの宿屋にもあるので、このまま回収します");
        }
        else
        {
            this.log.Write("Relay", detail);
        }

        // 暗転の隙間（Lifestream が終わってから宿屋に入るまで、移動の印が何も立っていなかった時間）。
        // InnTrip.SettleGrace（10秒）が足りているかを実機で確かめるために残す。
        this.log.Write("Relay", $"暗転の隙間: {this.innTrip.LongestSettleWait.TotalSeconds:F1} 秒");

        this.BeginCollect();
    }

    /// <summary>
    /// エリア移動・会話・読み込みの最中か。
    /// この間は「Lifestream が終わったのに宿屋に居ない」と数えない。
    ///
    /// 受付と話している間や暗転の最中に、どの印が立つかは実機で測っていない。
    /// 立ちそうな印を広めに見ておき、どれも立たない隙間は InnTrip.SettleGrace（10秒）で待つ。
    /// </summary>
    internal static bool IsTransitioning()
        => !Me.Available
           || Svc.Condition[ConditionFlag.BetweenAreas]
           || Svc.Condition[ConditionFlag.BetweenAreas51]
           || Svc.Condition[ConditionFlag.OccupiedInEvent]
           || Svc.Condition[ConditionFlag.OccupiedInQuestEvent]
           || Svc.Condition[ConditionFlag.Occupied]
           || Svc.Condition[ConditionFlag.Occupied33];

    // ------------------------------------------------------------------
    // 回収

    private void BeginCollect()
    {
        // 宿屋への移動は終わった。以後の中止で Lifestream を止めない
        // （止めると、そのとき Lifestream が受けている別の頼みごとまで消す）。
        this.innTrip.Reset();
        this.requestedInn = null;

        this.Current = Phase.Collecting;
        this.Detail = "呼び鈴を探しています";

        this.bell.Reset();
        this.bell.Begin();
        this.ownsMovement = true;
    }

    private void TickCollecting()
    {
        this.bell.Tick();
        this.Detail = this.bell.Detail;

        switch (this.bell.Current)
        {
            case BellRunner.Step.Done:
                this.ownsMovement = false;
                if (this.bell.Completion == BellCompletion.Collected)
                {
                    this.CollectedCount++;
                    this.FinishAttempt(true, "ベンチャーを回収しました");
                }
                else if (this.bell.Completion == BellCompletion.NothingToCollect)
                    this.FinishAttempt(true, "回収対象はありませんでした", noCollection: true);
                else
                    this.FinishAttempt(false, "回収の完了を確認できませんでした");
                break;

            case BellRunner.Step.Failed:
                this.ownsMovement = false;
                this.FinishAttempt(false, this.bell.Detail);
                break;
        }
    }

    // ------------------------------------------------------------------
    // 後始末と復帰

    /// <summary>
    /// 1回ぶんの回収を終える。結果に応じて自動採集へ戻す。
    /// </summary>
    private void FinishAttempt(bool success, string detail, bool noCollection = false)
    {
        this.LastResult = $"{DateTime.Now:HH:mm} {(noCollection ? "回収不要" : success ? "成功" : "失敗")}: {detail}";
        this.log.Write("Relay", this.LastResult);

        this.bell.Reset();
        this.innTrip.Reset();
        this.requestedInn = null;
        this.breakWaitStarted = null;

        CooldownReason = noCollection ? "回収対象がなかったため" : success ? "" : "失敗したため";
        if (noCollection)
        {
            this.nextAttemptAllowedAt = DateTime.UtcNow.Add(ShortRetryDelay);
        }
        else if (success)
        {
            // うまくいったので、失敗の連続数は忘れる。
            this.consecutiveFailures = 0;
        }
        else
        {
            this.consecutiveFailures++;

            // 失敗した直後に再挑戦すると、同じ理由で失敗し続けて
            // 採集と往復を繰り返すことになる。間隔を空ける。
            //
            // ただし1回目から長く空けない。
            // 呼び鈴がまだ読み込まれていない・ナビメッシュの構築が
            // 間に合わなかった、といった一度きりの失敗があり、
            // それで長く待つとベンチャーの回収が丸ごと遅れる。
            // 続けて失敗するなら環境の問題なので、そこで長く空ける。
            var cooldown = this.consecutiveFailures < FailuresBeforeLongWait
                ? ShortRetryDelay
                : TimeSpan.FromMinutes(Math.Max(1, this.config.RetryCooldownMinutes));

            this.nextAttemptAllowedAt = DateTime.UtcNow.Add(cooldown);

            this.log.Write("Relay",
                cooldown == ShortRetryDelay
                    ? $"{cooldown.TotalMinutes:F0} 分後にもう一度試します"
                        + $"（失敗 {this.consecutiveFailures} 回目）"
                    : $"{cooldown.TotalMinutes:F0} 分後まで回収を試しません"
                        + $"（失敗が {this.consecutiveFailures} 回続きました）");
        }

        var shouldResume = success ? this.config.ResumeAfterCollect : this.config.ResumeAfterFailure;

        if (!shouldResume)
        {
            this.Stop(noCollection ? "回収対象はありませんでした（自動採集は再開しない設定です）" : success
                ? "回収しました（自動採集は再開しない設定です）"
                : $"回収に失敗しました（{detail}）");
            return;
        }

        this.Current = Phase.Resuming;
        this.Detail = "自動採集へ戻しています";
        this.resumeIssued = false;
        this.resumeStartedAt = DateTime.UtcNow;
    }

    /// <summary>失敗が何回続いているか。成功したら 0 に戻す。</summary>
    private int consecutiveFailures;

    /// <summary>
    /// この回数までの失敗は、短い間隔でもう一度試す。
    ///
    /// 呼び鈴がまだ読み込まれていない、ナビメッシュの構築が間に合わない、
    /// といった一度きりの失敗があるため、1回目から長く待たない。
    /// </summary>
    private const int FailuresBeforeLongWait = 3;

    /// <summary>続けて失敗していないときの、次に試すまでの間隔。</summary>
    private static readonly TimeSpan ShortRetryDelay = TimeSpan.FromMinutes(2);

    private bool resumeIssued;
    private DateTime resumeStartedAt = DateTime.MinValue;

    /// <summary>自動採集を ON へ戻すのを待つ上限。</summary>
    private static readonly TimeSpan ResumeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 自動採集へ戻す。
    ///
    /// リテイナーの画面が閉じきっていることを確かめてから ON にする。
    /// 開いたままだと GBR が動けない。
    /// </summary>
    private void TickResuming()
    {
        if (BellRunner.IsBellSessionOpen())
        {
            this.Detail = "リテイナーの画面が閉じるのを待っています";

            if (DateTime.UtcNow - this.resumeStartedAt > ResumeTimeout)
            {
                this.Stop("リテイナーの画面が閉じないため、自動採集へ戻せませんでした");
                return;
            }

            return;
        }

        if (!this.resumeIssued)
        {
            if (!this.SetGatherBuddy(true))
            {
                this.Stop("自動採集へ戻せませんでした");
                return;
            }

            this.resumeIssued = true;
            this.log.Write("Relay", "自動採集を再開しました");
            return;
        }

        // 実際に ON になったか読み直して確かめる。
        // 「呼べた」ことと「変わった」ことは別物。
        var enabled = this.gatherBuddy.IsAutoGatherEnabled();

        if (enabled == true)
        {
            this.Current = Phase.Watching;
            this.Detail = "自動採集へ戻りました。ベンチャーの状態を見ています";
            this.nextVentureCheck = DateTime.UtcNow.Add(VentureCheckInterval);
            return;
        }

        if (DateTime.UtcNow - this.resumeStartedAt > ResumeTimeout)
            this.Stop("自動採集が ON になりませんでした");
    }

    // ------------------------------------------------------------------
    // 画面から使うもの

    /// <summary>次に回収を試せる時刻まであと何分か。0 なら今すぐ試せる。</summary>
    public string CooldownReason { get; private set; } = "";

    public double CooldownMinutesLeft
    {
        get
        {
            var left = this.nextAttemptAllowedAt - DateTime.UtcNow;
            return left > TimeSpan.Zero ? left.TotalMinutes : 0;
        }
    }

    /// <summary>失敗の待ち時間を今すぐ解除する。</summary>
    public void ClearCooldown()
    {
        this.nextAttemptAllowedAt = DateTime.MinValue;
        CooldownReason = "";

        // 失敗の連続数も忘れる。
        // 解除したのに「まだ失敗続きだから次も長く待つ」では意図に反する。
        this.consecutiveFailures = 0;

        this.log.Write("Relay", "再挑戦までの待ち時間を解除しました");
    }

    /// <summary>
    /// いますぐ回収へ向かう（画面のボタンから使う）。
    /// 採集の切れ目は待たずに、危なくない状態になり次第で抜ける。
    /// </summary>
    public void ForceCollectNow()
    {
        if (!this.Active)
        {
            this.log.Write("Relay", "監視していないため、回収へ向かいません");
            return;
        }

        if (this.Current is not (Phase.Watching or Phase.WaitingForBreak))
        {
            this.log.Write("Relay", "すでに回収の処理中です");
            return;
        }

        if (this.CollectionBlocked())
        {
            this.forceCollectionPending = true;
            this.Detail = "精選が終わったら回収へ向かいます";
            return;
        }
        this.forceCollectionPending = false;
        this.ClearCooldown();
        this.Current = Phase.WaitingForBreak;

        this.breakWaitStarted = DateTime.UtcNow;

        this.log.Write("Relay", "手動の指示により、回収へ向かいます");
    }
}
