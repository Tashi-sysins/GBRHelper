using System;
using System.Collections.Generic;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using GBRHelper.Features;
using GBRHelper.Ipc;
using GBRHelper.Ui;

namespace GBRHelper;

/// <summary>
/// GatherBuddyReborn の自動採集中に、ベンチャーを回収して戻ってくる。
///
/// 流れ:
///   ベンチャー回収可能を検知
///     → 採集ノードの切れ目で自動採集を OFF
///     → 帰還先の宿屋の部屋へ入る（Lifestream の宿屋機能）
///     → 部屋の呼び鈴へ移動・アクセス
///     → AutoRetainer が回収
///     → リテイナーの画面を閉じる
///     → 自動採集を ON に戻す
///
/// GatherBuddyReborn 本体には手を加えていない。
/// GBR が公開している IPC（GatherBuddy/Plugin/GatherBuddyIpc.cs）だけを使う。
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/gbrhelper";
    private const string CommandAlias = "/gbh";

    private readonly WindowSystem windows = new("GBRHelper");

    private readonly Configuration config;
    private readonly CharacterConfigStore configStore;
    private readonly ArtisanFeature artisanFeature;
    private bool ready;
    private readonly RunLog log;
    private readonly TimedFeature timedFeature;
    private readonly GatherProfileController gatherProfiles;

    private readonly GatherBuddyIpc gatherBuddy;
    private readonly AutoRetainerIpc retainer;
    private readonly LifestreamIpc lifestream;
    private readonly VnavmeshIpc navmesh;

    private readonly BellRunner bell;
    private readonly InnService inns;
    private readonly RelayController relay;
    private readonly InnTestRunner innTest;
    private readonly GbrConflictGuard gbrGuard;

    private readonly FeatureCatalog features;
    private readonly TranslationFeature translation;
    private readonly MainWindow window;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Svc>();

        this.configStore = new CharacterConfigStore(pluginInterface.GetPluginConfigDirectory(), pluginInterface.ConfigFile.FullName);
        this.config = configStore.Config;
        this.log = new RunLog();

        this.gatherBuddy = new GatherBuddyIpc();
        this.retainer = new AutoRetainerIpc();
        this.lifestream = new LifestreamIpc();
        this.navmesh = new VnavmeshIpc();

        this.bell = new BellRunner(this.navmesh, this.log, this.retainer);
        this.inns = new InnService();

        this.relay = new RelayController(
            this.config, this.log, this.gatherBuddy, this.retainer,
            this.lifestream, this.navmesh, this.bell, this.inns);

        // 検証（デバッグ用）。呼び鈴の部品は回収とは別に持つ（状態を取り合わないため）。
        this.innTest = new InnTestRunner(
            this.lifestream, this.inns,
            new BellRunner(this.navmesh, this.log, this.retainer),
            this.log);

        // GBR の設定のうち、回収と競合するものを動いている間だけ競合しない値に保つ。
        var gbrConfigAccess = new GbrConfigAccess();
        this.gbrGuard = new GbrConflictGuard(gbrConfigAccess, this.log);

        // 【機能の目録】新機能はここに Add する（左ペインの並び順は SortOrder で決める）。
        this.features = new FeatureCatalog();

        // 機能②：ベンチャー依頼品の解放採取（Lv 帯ごとのボタンで、未採取の品を GBR の自動採集リストへ登録する）
        var listAccess = new GbrAutoGatherListAccess(gbrConfigAccess);
        var catalogBuilder = new LiveCatalogBuilder(gbrConfigAccess);
        var completionReader = new GatheringCompletionReader(new LiveCompletionEffects());
        var timedAccess = new GbrTimedAccess(gbrConfigAccess);
        var timed = new TimedFeature(this.config, catalogBuilder, completionReader, listAccess,
            this.gatherBuddy, timedAccess,
            () => this.innTest.Running || this.relay.Current is not (RelayController.Phase.Off or RelayController.Phase.Watching));
        this.timedFeature = timed;
        this.features.Add(timed);
        // ほかの自動処理で、解放採取・全素材の補充のリストの登録・作り直しを止める理由（止めなくてよければ null）。
        // 2026-10-05：解放採取が霊砂の管理リストで止まっているのに「ベンチャー回収・宿屋の検証が動いている」と出ていたので、
        // 理由ごとに文を分けた。ベンチャーの見張り（Watching）だけなら止めない。
        string? BusyReason()
        {
            if (this.innTest.Running || this.relay.Current is not (RelayController.Phase.Off or RelayController.Phase.Watching))
                return GatherProfileController.BusyText;
            return timed.Running ? TimedFeature.SessionBlockText : null;
        }

        this.gatherProfiles = new GatherProfileController(this.config, catalogBuilder, completionReader, listAccess,
            this.gatherBuddy, timedAccess, () => BusyReason() is not null);

        // 現在のキャラクターだけ操作する。旧 Link の送信・受信・Mirror は起動しない。
        this.innTest.StartBlocked = () => !ready || InnTestRunner.Blocked(relay.Active, gatherBuddy.IsAutoGatherEnabled());
        this.gatherProfiles.StopHandler = this.StopAutoGather;
        this.gatherProfiles.StopBlock = this.StopBlock;
        BindAutomation(this.relay, timed, this.gatherBuddy);
        this.relay.CharacterReady = () => ready && configStore.Character == CurrentCharacter;

        var unvisited = new UnvisitedFeature(
            this.config, this.log, catalogBuilder, completionReader, listAccess,
            this.gatherBuddy, BusyReason, this.gatherProfiles);
        this.features.Add(unvisited);
        var stock = new StockFeature(catalogBuilder, completionReader, listAccess,
            this.gatherBuddy, timedAccess, BusyReason, this.gatherProfiles);
        this.features.Add(stock);

        // Crafting Listsから末端素材抽出（GBR の「Artisan から読み込む」と同じ中身を GBRHelper の画面から）。
        this.artisanFeature = new ArtisanFeature(new ArtisanListAccess(), listAccess, this.gatherBuddy, BusyReason, config);
        this.features.Add(this.artisanFeature);

        // 機能①：GBR の日本語表示（常に ON。GBR のメイン画面の英語を日本語に差し替える）。
        // フックの付け外しは描画のたびに OnDraw で行う（ImGui を描くスレッドの上で行うため）。
        this.translation = new TranslationFeature(this.config);
        this.features.Add(this.translation);

        // デバッグ（左上の「機能」を 5 回続けて押すと出る。日本語表示の状態と、訳の無かった英語を集める機能）。
        this.features.Add(new DebugFeature(this.translation));

        this.window = new MainWindow(
            this.config, this.relay, this.log, this.gatherBuddy,
            this.retainer, this.lifestream, this.navmesh, this.inns, this.innTest, this.gbrGuard,
            this.features);

        this.windows.AddWindow(this.window);

        Svc.PluginInterface.UiBuilder.Draw += this.OnDraw;
        Svc.PluginInterface.UiBuilder.OpenConfigUi += this.OpenWindow;
        Svc.PluginInterface.UiBuilder.OpenMainUi += this.OpenWindow;

        Svc.Commands.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "GBRHelper の画面を開きます。",
        });

        Svc.Commands.AddHandler(CommandAlias, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "GBRHelper の画面を開きます（別名）。",
        });

        Svc.Framework.Update += this.OnUpdate;

        this.log.Write("Info", "読み込みました");

    }

    public static void BindAutomation(RelayController relay, TimedFeature timed, GatherBuddyIpc gatherBuddy)
    {
        relay.CollectionBlocked = () => timed.BlocksRelay;
        gatherBuddy.EnabledChanged += timed.OnGatherBuddyEnabledChanged;
    }

    private static ulong CurrentCharacter => Svc.PlayerState.IsLoaded ? Svc.PlayerState.ContentId : 0;

    private bool EnsureCharacter()
    {
        var cid = CurrentCharacter;
        if (cid == 0)
        {
            // ログアウト中はファイルに書かない。旧設定は保持し、次のログインで先に保存する。
            if (ready) { ready = false; ResetCharacterState(); }
            return false;
        }
        if (cid == configStore.Character && ready) return true;
        ready = false;
        ResetCharacterState();
        configStore.Switch(cid);
        if (cid == 0) return false;
        if (inns.MigrateFromCity(config) is { } migrated) log.Write("設定", migrated);
        window.ResetCharacter();
        ready = true;
        SyncWithGatherBuddyOnce();
        if (config.OpenOnStartup) window.IsOpen = true;
        return true;
    }

    private void ResetCharacterState()
    {
        // 外部プラグインには指示せず、このゲーム内の予約・キャッシュだけ破棄する。
        relay.ResetCharacter(); innTest.ResetCharacter(); timedFeature.ResetCharacter();
        gatherProfiles.ResetCharacter(); artisanFeature.ResetCharacter();
        foreach (var feature in features.Items)
        {
            if (feature is StockFeature stock) stock.ResetCharacter();
            if (feature is UnvisitedFeature unlock) unlock.ResetCharacter();
        }
    }

    /// <summary>
    /// 起動直後に GBR の状態へ合わせる。
    /// GBR がまだ起動していなければ何もしない（その場合は以後のイベントで拾える）。
    /// </summary>
    private void SyncWithGatherBuddyOnce()
    {
        this.gatherBuddy.TrySubscribeEnabledChanged();

        // ベンチャー回収の「有効」が ON のときだけ。
        if (this.gatherBuddy.IsAutoGatherEnabled() == true && this.config.VentureRelayOn)
        {
            this.log.Write("GBR", "すでに自動採集が ON でした。ベンチャーの監視を始めます");
            this.relay.Start();
        }
    }

    private void OnUpdate(IFramework framework)
    {
        try
        {
            if (!EnsureCharacter()) return;
            // 検証を先に進める。GBR の自動採集が ON になったフレームで、
            // 回収の流れが Lifestream を使い始める前に検証のほうが引くため。
            this.innTest.Tick(this.relay.Active || this.gatherBuddy.IsAutoGatherEnabled() != false);

            // 回収の流れより先に確かめる。動き出したフレームで、GBR が AutoRetainer を待ち始める前に止めるため。
            this.gbrGuard.Tick(this.relay.Active);

            // 精選キューをベンチャー回収のOFF操作で中断しない。
            this.gatherProfiles.Tick();
            if (ProfileUpdateCycle.AllowRelayTick(this.timedFeature.BlocksRelay, this.gatherProfiles.IsWriting)) this.relay.Tick();

            // Enabled になっている新機能の Tick を回す。1個の例外で他を止めない。
            this.features.TickEnabled();

        }
        catch (Exception ex)
        {
            // 毎フレーム走るので、例外をそのまま上げるとログが埋まる。
            // ここで止めて、処理そのものは次のフレームで続ける。
            Svc.Log.Error($"[GBRHelper] 処理中に例外が出ました: {ex}");
        }
    }

    /// <summary>
    /// 「Auto-Gatherに停止」（GBR の自動採集を止める）。
    /// ベンチャー回収の見張りを先に止める（回収の途中なら、回収が終わったあとに GBR を ON に戻さないように）。そのあと GBR の自動採集を OFF にする。
    /// GBR の画面の停止と同じく、すぐ止める（採集の切れ目は待たない）。
    /// </summary>
    private string StopAutoGather()
    {
        var on = this.gatherBuddy.IsAutoGatherEnabled();
        if (on is null) return "GatherBuddyReborn の状態を読めません";
        var relayWasActive = this.relay.Active;
        if (relayWasActive) this.relay.Stop("利用者が「Auto-Gatherに停止」を押しました");
        if (on == false)
            return relayWasActive ? "ベンチャー回収の見張りを止めました（GBR の自動採集は止まっています）" : GatherProfileController.StoppedText;
        var ok = this.gatherBuddy.SetAutoGatherEnabled(false);
        this.log.Write("GBR", ok ? "「Auto-Gatherに停止」で自動採集を止めました" : "「Auto-Gatherに停止」：自動採集を止められませんでした");
        return ok ? "GBR の自動採集を止めました" : "GBR の自動採集を止められませんでした";
    }

    /// <summary>このゲームの「Auto-Gatherに停止」を押せない理由。押せるなら null。</summary>
    private string? StopBlock()
    {
        if (Svc.PlayerState.ContentId == 0) return "ログインしていません";
        return this.gatherBuddy.IsAutoGatherEnabled() switch
        {
            null => "GBR の状態を読めません",
            false when !this.relay.Active => GatherProfileController.StoppedText,
            _ => null,
        };
    }

    private void OnDraw()
    {
        if (!ready || configStore.Character != CurrentCharacter) { translation.Dispose(); return; }
        // 翻訳のフックの付け外しは、ImGui を描くスレッドの上（ここ）で行う。
        // 1 個の機能の例外で自分の画面が描けなくならないように囲う。
        try
        {
            this.translation.OnDraw();
        }
        catch (Exception ex)
        {
            Svc.Log.Error($"[GBRHelper] 翻訳の準備で例外: {ex}");
        }

        this.windows.Draw();
    }

    private void OnCommand(string command, string args)
    {
        var arg = args.Trim();

        switch (arg)
        {
            case "":
                this.window.IsOpen = !this.window.IsOpen;
                break;

            case "now":
                if (ready && configStore.Character == CurrentCharacter) this.relay.ForceCollectNow();
                break;

            default:
                Svc.Chat.Print(
                    $"使い方: {CommandName}（画面の開閉） / {CommandName} now（いますぐ回収へ向かう）");
                break;
        }
    }

    private void OpenWindow()
        => this.window.IsOpen = true;

    public void Dispose()
    {
        Svc.Framework.Update -= this.OnUpdate;
        Svc.Commands.RemoveHandler(CommandName);
        Svc.Commands.RemoveHandler(CommandAlias);

        Svc.PluginInterface.UiBuilder.Draw -= this.OnDraw;
        Svc.PluginInterface.UiBuilder.OpenConfigUi -= this.OpenWindow;
        Svc.PluginInterface.UiBuilder.OpenMainUi -= this.OpenWindow;

        this.windows.RemoveAllWindows();
        this.window.Dispose();

        // 翻訳のフックを外す（自分が置いたフックだけ。GBR には触れない）。
        this.translation.Dispose();

        // 【アンロード経路では相手のプラグインに触れない】
        // ここで GatherBuddyReborn を操作すると、こちらを更新・再読み込みしただけで
        // 相手の自動採集が止まる。RelayController.Dispose も同じ方針で書いてある。
        this.innTest.Dispose();
        this.relay.Dispose();
        this.gatherBuddy.Dispose();

        // 精選の中断通知の購読を解除する。
        this.gatherBuddy.EnabledChanged -= this.timedFeature.OnGatherBuddyEnabledChanged;
    }
}
