using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using GBRHelper.Features;
using GBRHelper.Ipc;

namespace GBRHelper.Ui;

/// <summary>
/// 操作と状態表示の画面。
///
/// ベンチャー回収は「有効」のチェックが ON で、かつ GBR の自動採集中だけ動く（要望で、
/// GBR の ON/OFF だけに連動する作りから変えた）。GBR の自動採集そのものの切り替えは GBR 側で行う。
/// </summary>
public sealed class MainWindow : Window, IDisposable
{
    private readonly Configuration config;
    private readonly RelayController relay;
    private readonly RunLog log;
    private readonly GatherBuddyIpc gatherBuddy;
    private readonly AutoRetainerIpc retainer;
    private readonly LifestreamIpc lifestream;
    private readonly VnavmeshIpc navmesh;
    private readonly InnService inns;
    private readonly InnTestRunner innTest;
    private readonly GbrConflictGuard gbrGuard;
    private readonly FeatureCatalog features;

    /// <summary>
    /// 検証タブを出しているか。既定は隠す。
    /// 記録タブを 5 回続けてクリックすると出る（もう一度 5 回で隠れる）。保存はしない（読み直すと隠れる）。
    /// </summary>
    private bool showTestTab;

    /// <summary>記録タブのクリックを数える。</summary>
    private readonly TapCounter logTabTaps = new();

    /// <summary>左上の「機能」の文字のクリックを数える（5 回で「デバッグ」を出す・隠す）。</summary>
    private readonly TapCounter featureTaps = new();

    /// <summary>左ペインに「デバッグ」を出しているか。既定は隠す。保存しない（読み直すと隠れる）。</summary>
    private bool showDebug;

    /// <summary>検証で、着いたら呼び鈴を開くところまで確かめるか。保存はしない（検証のたびに選ぶ）。</summary>
    private bool testOpenBell = true;

    /// <summary>
    /// 「ベンチャー回収」機能の内部キー。左ペインではこの名前で選ばれる。
    /// Configuration.LastSelectedFeature に書くときもこの値を使う。
    /// </summary>
    public const string VentureRelayKey = "ベンチャー回収";

    /// <summary>左の一覧の一番上「必要なプラグイン」の内部キー（Configuration.LastSelectedFeature にもこの値を書く）。</summary>
    public const string RequiredPluginsKey = "必要なプラグイン";

    /// <summary>いま右ペインに出している機能の名前。</summary>
    public void ResetCharacter() => selected = string.IsNullOrEmpty(config.LastSelectedFeature) ? VentureRelayKey : config.LastSelectedFeature;

    private string selected = VentureRelayKey;

    /// <summary>使うプラグインの状態（2 秒ごとに読み直す）。</summary>
    private List<RequiredPlugins.State> pluginStates = [];

    /// <summary>次に導入済みの一覧を読む時刻。</summary>
    private DateTime nextPluginCheck;

    /// <summary>導入済みの一覧を読めなかった理由（読めたら null）。</summary>
    private string? pluginListError;

    public MainWindow(
        Configuration config,
        RelayController relay,
        RunLog log,
        GatherBuddyIpc gatherBuddy,
        AutoRetainerIpc retainer,
        LifestreamIpc lifestream,
        VnavmeshIpc navmesh,
        InnService inns,
        InnTestRunner innTest,
        GbrConflictGuard gbrGuard,
        FeatureCatalog features)
        : base("GBRHelper##GBRHelper")
    {
        this.config = config;
        this.relay = relay;
        this.log = log;
        this.gatherBuddy = gatherBuddy;
        this.retainer = retainer;
        this.lifestream = lifestream;
        this.navmesh = navmesh;
        this.inns = inns;
        this.innTest = innTest;
        this.gbrGuard = gbrGuard;
        this.features = features;

        if (!string.IsNullOrEmpty(this.config.LastSelectedFeature))
            this.selected = this.config.LastSelectedFeature;

        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(760, 480),
            MaximumSize = new Vector2(1600, 1400),
        };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        // 左右2ペインに分ける。左は機能一覧（220px）、右は中身。
        var leftWidth = 220f * ImGuiHelpers.GlobalScale;
        var availHeight = ImGui.GetContentRegionAvail().Y;

        using (var left = ImRaii.Child("##leftPane", new Vector2(leftWidth, availHeight), border: true))
        {
            if (left)
                this.DrawLeftPane();
        }

        ImGui.SameLine();

        using (var right = ImRaii.Child("##rightPane", new Vector2(-1, availHeight), border: true))
        {
            if (right)
                this.DrawRightPane();
        }
    }

    // ------------------------------------------------------------------
    // 左ペイン（機能一覧）

    private void DrawLeftPane()
    {
        // 一番上に「必要なプラグイン」（要望：必要なプラグインが分かるように、分かりやすい場所へ）。
        // 足りなければ数を添える。必須（GBR）が使えなければ赤、ほかが使えなければ黄色。
        this.RefreshPluginStates();
        var unavailable = RequiredPlugins.Unavailable(this.pluginStates);
        var labelColor = RequiredPlugins.RequiredUnavailable(this.pluginStates) ? ImGuiColors.DalamudRed : ImGuiColors.DalamudYellow;
        using (ImRaii.PushColor(ImGuiCol.Text, labelColor, unavailable > 0))
        {
            if (ImGui.Selectable(RequiredPlugins.LeftLabel(unavailable) + "##leftPlugins", this.selected == RequiredPluginsKey))
                this.Select(RequiredPluginsKey);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("GBRHelper が使うほかのプラグインと、いま使えるかを出します。");

        ImGui.Separator();

        ImGui.TextColored(ImGuiColors.DalamudGrey, "機能");

        // 「機能」の文字を 5 回続けて押すと「デバッグ」を出す（もう一度 5 回で隠す。保存しない）。
        if (ImGui.IsItemClicked() && this.featureTaps.Tap(DateTime.UtcNow))
        {
            this.showDebug = !this.showDebug;
            if (!this.showDebug && this.selected == DebugFeature.FeatureName)
                this.Select(VentureRelayKey);
        }

        ImGui.Separator();

        // 並び順（SortOrder）が負の機能は「ベンチャー回収」の上に置く（要望：「GBR の日本語表示」をベンチャー回収の上へ）。
        foreach (var f in this.features.Items.Where(f => f.SortOrder < 0 && (f is not DebugFeature || this.showDebug)))
        {
            this.DrawFeatureItem(f);
            ImGui.Separator();
        }

        // 「ベンチャー回収」の見出し（元々の機能。「有効」のチェックは右側に置く）。
        if (ImGui.Selectable(VentureRelayKey + "##leftVR", this.selected == VentureRelayKey))
            this.Select(VentureRelayKey);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("GatherBuddyReborn の自動採集中にベンチャーを回収して戻ります。");

        ImGui.Separator();

        // 新機能（IFeature 実装）。項目のあいだに区切り線を引く（要望：「ベンチャー回収」と同じく各項目の上に線）。
        var first = true;
        foreach (var f in this.features.Items.Where(f => f.SortOrder >= 0))
        {
            if (f is DebugFeature && !this.showDebug)
                continue;

            if (!first)
                ImGui.Separator();
            first = false;
            this.DrawFeatureItem(f);
        }
    }

    /// <summary>左の一覧の 1 項目（有効のチェック＋名前。乗せると説明）。</summary>
    private void DrawFeatureItem(IFeature f)
    {
        {
            var isSelected = this.selected == f.Name;
            var enabled = f.Enabled;

            if (!f.HideEnableToggle)
            {
                // チェックボックスでオン・オフ。チェックの変更は副作用を呼ぶ。
                if (ImGui.Checkbox("##cb_" + f.Name, ref enabled))
                {
                    f.Enabled = enabled;
                    if (enabled)
                        f.OnEnabled();
                    else
                        f.OnDisabled();

                    this.config.Save();
                }

                ImGui.SameLine();
            }

            if (ImGui.Selectable(f.Name + "##left_" + f.Name, isSelected))
                this.Select(f.Name);

            if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(f.Description))
                ImGui.SetTooltip(f.Description);
        }
    }

    private void Select(string name)
    {
        if (this.selected == name)
            return;

        this.selected = name;
        this.config.LastSelectedFeature = name;
        this.config.Save();
    }

    // ------------------------------------------------------------------
    // 右ペイン（選ばれた機能の中身）

    private void DrawRightPane()
    {
        if (this.selected == VentureRelayKey)
        {
            this.DrawVentureRelayPane();
            return;
        }

        if (this.selected == RequiredPluginsKey)
        {
            this.DrawRequiredPluginsPane();
            return;
        }

        // 隠しているデバッグは選ばない（前回デバッグを選んだまま閉じても、読み直すとベンチャー回収に戻る）。
        var feature = this.features.Items.FirstOrDefault(f => f.Name == this.selected && (f is not DebugFeature || this.showDebug));
        if (feature == null)
        {
            // 保存されていた名前の機能が無い（機能名を変えた・機能を外した）。
            // 「見つかりません」を出し続けず、元からある「ベンチャー回収」に戻す。
            this.Select(VentureRelayKey);
            this.DrawVentureRelayPane();
            return;
        }

        if (feature.ShowHeader)
        {
            ImGui.TextUnformatted(feature.Name);

            if (!string.IsNullOrEmpty(feature.Description))
                ImGui.TextColored(ImGuiColors.DalamudGrey, feature.Description);

            ImGui.Separator();
        }

        if (!feature.Enabled && !feature.HideEnableToggle)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow,
                "この機能は無効です。左ペインのチェックボックスで有効にしてください。");
            return;
        }

        feature.DrawRight();
    }

    // ------------------------------------------------------------------
    // 「必要なプラグイン」（左の一覧の一番上）

    /// <summary>導入済みの一覧を読み、使うプラグインの状態を作り直す（2 秒に 1 回）。</summary>
    private void RefreshPluginStates()
    {
        if (DateTime.UtcNow < this.nextPluginCheck)
            return;

        this.nextPluginCheck = DateTime.UtcNow.AddSeconds(2);

        try
        {
            this.pluginStates = RequiredPlugins.Check(Svc.PluginInterface.InstalledPlugins
                .Select(p => (p.InternalName, p.IsLoaded, (string?)p.Version.ToString())));
            this.pluginListError = null;
        }
        catch (Exception ex)
        {
            // 読めなければ前の結果のまま。記録は理由が変わったときだけ（2 秒ごとに記録を埋めない）。
            var reason = ex.GetBaseException().Message;
            if (reason != this.pluginListError)
                Svc.Log.Warning($"[GBRHelper] 導入済みのプラグインの一覧を読めません: {reason}");
            this.pluginListError = reason;
        }
    }

    private void DrawRequiredPluginsPane()
    {
        this.RefreshPluginStates();

        ImGui.TextUnformatted("必要なプラグイン");
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            ImGui.TextWrapped("GBRHelper が使うほかのプラグインです。「必須」が無いと何も動きません。ほかは、その機能を使うときだけ要ります。");
        ImGui.Separator();

        if (this.pluginListError is not null)
            ImGui.TextColored(ImGuiColors.DalamudYellow, "導入済みのプラグインの一覧を読めません：" + this.pluginListError);

        using (var table = ImRaii.Table("##requiredPlugins", 4,
                   ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
        {
            if (table)
            {
                ImGui.TableSetupColumn("プラグイン", ImGuiTableColumnFlags.WidthStretch, 1.1f);
                ImGui.TableSetupColumn("状態", ImGuiTableColumnFlags.WidthStretch, 1.1f);
                ImGui.TableSetupColumn("使う機能", ImGuiTableColumnFlags.WidthStretch, 1.6f);
                ImGui.TableSetupColumn("無いとき", ImGuiTableColumnFlags.WidthStretch, 2.2f);
                ImGui.TableHeadersRow();

                foreach (var state in this.pluginStates)
                {
                    var required = state.Entry.Need == RequiredPlugins.Need.Required;

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(state.Entry.DisplayName);
                    ImGui.TextColored(ImGuiColors.DalamudGrey, required ? "必須" : "機能によって必要");

                    ImGui.TableNextColumn();
                    var color = state.Status switch
                    {
                        RequiredPlugins.Status.Loaded => ImGuiColors.HealerGreen,
                        RequiredPlugins.Status.Missing when required => ImGuiColors.DalamudRed,
                        _ => ImGuiColors.DalamudYellow,
                    };
                    ImGui.TextColored(color, RequiredPlugins.StatusText(state));

                    ImGui.TableNextColumn();
                    ImGui.TextWrapped(state.Entry.UsedBy);

                    ImGui.TableNextColumn();
                    ImGui.TextWrapped(state.Entry.IfMissing);
                }
            }
        }

        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
        {
            ImGui.TextWrapped("「導入済み・停止中」は、Dalamud のプラグインの一覧（/xlplugins）で無効になっているか、読み込みに失敗しています。");
            ImGui.TextWrapped("ベンチャー回収の通信の失敗やナビメッシュの状態は、「ベンチャー回収」の「必要なプラグイン」タブに出ます。");
        }
    }

    // ------------------------------------------------------------------
    // 「ベンチャー回収」機能の右ペイン（既存の UI をそのまま呼ぶ）

    private void DrawVentureRelayPane()
    {
        // 「有効」のチェック（要望：GBR の ON/OFF に連動するのではなく、ここで有効にする）。
        // ON で、かつ GBR の自動採集中だけ回収に行く。回収の途中（宿屋へ移動中など）は切り替えない。
        var enabled = this.config.VentureRelayOn;
        using (ImRaii.Disabled(this.relay.Current is not (RelayController.Phase.Off or RelayController.Phase.Watching)))
        {
            if (ImGui.Checkbox("有効##ventureRelayEnabled", ref enabled))
            {
                this.config.VentureRelayOn = enabled;
                this.config.Save();
                this.relay.ApplyEnabled(this.gatherBuddy.IsAutoGatherEnabled() == true);
            }
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("ON にすると、GBR の自動採集中にベンチャーが溜まったら、採集の切れ目で宿屋へ行って回収し、採集に戻ります。\nOFF なら回収しません。");

        ImGui.Separator();
        this.DrawStatus();
        ImGui.Separator();

        using var tabs = ImRaii.TabBar("##vrTabs");

        if (!tabs)
            return;

        this.DrawSettingsTab();
        this.DrawPluginsTab();
        this.DrawLogTab();

        // 検証の途中で隠すと中止のボタンまで消えるので、動いている間は出したままにする。
        if (this.showTestTab || this.innTest.Running)
            this.DrawTestTab();
    }

    // ------------------------------------------------------------------

    private void DrawStatus()
    {
        var (label, color) = this.relay.Current switch
        {
            RelayController.Phase.Off
                => ("停止中", ImGuiColors.DalamudGrey),
            RelayController.Phase.Watching
                => ("監視中", ImGuiColors.HealerGreen),
            RelayController.Phase.WaitingForBreak
                => ("採集の切れ目を待っています", ImGuiColors.DalamudYellow),
            RelayController.Phase.Returning
                => ("宿屋へ移動中", ImGuiColors.DalamudYellow),
            RelayController.Phase.Collecting
                => ("回収中", ImGuiColors.DalamudYellow),
            RelayController.Phase.Resuming
                => ("採集へ復帰中", ImGuiColors.DalamudYellow),
            _ => ("不明", ImGuiColors.DalamudGrey),
        };

        ImGui.TextUnformatted("状態:");
        ImGui.SameLine();
        ImGui.TextColored(color, label);

        ImGui.TextUnformatted(this.relay.Detail);

        // GBR の状態も並べて出す。どちらが動いているかを取り違えないため。
        var gbrEnabled = this.gatherBuddy.IsAutoGatherEnabled();

        ImGui.TextUnformatted("GatherBuddyReborn:");
        ImGui.SameLine();

        if (gbrEnabled is null)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "状態を読めません");
        }
        else
        {
            ImGui.TextColored(
                gbrEnabled.Value ? ImGuiColors.HealerGreen : ImGuiColors.DalamudGrey,
                gbrEnabled.Value ? "自動採集 ON" : "自動採集 OFF");

            var status = this.gatherBuddy.StatusText();

            if (!string.IsNullOrEmpty(status))
            {
                ImGui.SameLine();
                ImGui.TextColored(ImGuiColors.DalamudGrey, $"（{status}）");
            }
        }

        if (!this.gatherBuddy.Subscribed)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow,
                "GatherBuddyReborn からの切り替え通知をまだ受け取れていません。");
            ImGui.TextColored(ImGuiColors.DalamudGrey,
                "GatherBuddyReborn が起動していれば、数秒で購読します。");
        }

        if (this.relay.CollectedCount > 0 || !string.IsNullOrEmpty(this.relay.LastResult))
        {
            ImGui.TextUnformatted($"回収した回数: {this.relay.CollectedCount}");

            if (!string.IsNullOrEmpty(this.relay.LastResult))
                ImGui.TextColored(ImGuiColors.DalamudGrey, this.relay.LastResult);
        }

        var cooldown = this.relay.CooldownMinutesLeft;

        if (cooldown > 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow,
                $"{this.relay.CooldownReason}、あと {cooldown:F0} 分は回収を試しません。");
            ImGui.SameLine();

            if (ImGui.Button("いま解除する"))
                this.relay.ClearCooldown();
        }

        if (this.relay.Active
            && this.relay.Current is RelayController.Phase.Watching
                or RelayController.Phase.WaitingForBreak)
        {
            if (ImGui.Button("いますぐ回収へ向かう"))
                this.relay.ForceCollectNow();

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "採集の切れ目を待たずに回収へ向かいます。\n"
                    + "戦闘中などの危ない状態では、それが終わるまで待ちます。");
            }
        }
    }

    // ------------------------------------------------------------------

    private void DrawSettingsTab()
    {
        using var tab = ImRaii.TabItem("設定");

        if (!tab)
            return;

        ImGui.TextWrapped(
            "「有効」にチェックを入れると、GBR の自動採取中にベンチャー回収を行います。");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        this.DrawInn();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var resume = this.config.ResumeAfterCollect;

        if (ImGui.Checkbox("回収したら自動採集へ戻る", ref resume))
        {
            this.config.ResumeAfterCollect = resume;
            this.config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("切ると、回収したあとそのまま止まります。");

        var resumeFail = this.config.ResumeAfterFailure;

        if (ImGui.Checkbox("回収に失敗しても自動採集へ戻る", ref resumeFail))
        {
            this.config.ResumeAfterFailure = resumeFail;
            this.config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "失敗の多くは「呼び鈴が見つからない」「経路が引けない」で、\n"
                + "採集そのものは続けられます。切ると失敗した時点で止まります。");
        }

        ImGui.Spacing();

        var cooldown = this.config.RetryCooldownMinutes;
        ImGui.SetNextItemWidth(120);

        if (ImGui.InputInt("失敗したあと再挑戦まで待つ時間（分）", ref cooldown))
        {
            this.config.RetryCooldownMinutes = Math.Clamp(cooldown, 1, 600);
            this.config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "失敗した直後に再挑戦すると、同じ理由で失敗し続けて\n"
                + "採集と往復を繰り返します。間隔を空けます。");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        this.DrawGbrGuard();
    }

    /// <summary>
    /// 競合を避けるために GBR の設定をどうしているかを出す。
    /// 何を書き換えるのかが画面から分かるようにする（黙って相手の設定を変えない）。
    /// </summary>
    private void DrawGbrGuard()
    {
        // 要望：「Wait for AutoRetainer Multi-mode：OFF」とだけ書く（保つ値。説明の文は出さない）。
        foreach (var rule in GbrConflictGuard.Rules)
        {
            var name = rule.Label.Split('（')[0];
            ImGui.TextUnformatted($"{name}：{GbrConflictGuard.OnOff(rule.Required)}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(rule.Reason);
        }

        if (!string.IsNullOrEmpty(this.gbrGuard.LastError))
            ImGui.TextColored(ImGuiColors.DalamudYellow, this.gbrGuard.LastError);
    }

    /// <summary>「Lifestream に任せる」の表示名。</summary>
    private const string InnAutoLabel = "自動（どの宿屋にするかを Lifestream に任せる）";

    /// <summary>帰還先の宿屋を選ばせる。</summary>
    private void DrawInn()
    {
        ImGui.TextUnformatted("帰還先の宿屋");

        var current = this.config.InnAuto
            ? InnAutoLabel
            : string.IsNullOrEmpty(this.config.InnName)
                ? "（未設定）"
                : this.config.InnName;

        var all = this.inns.All();

        if (all.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed,
                $"宿屋の一覧を作れませんでした（{this.inns.BuildError}）。");
            ImGui.TextUnformatted($"いまの設定: {current}");
            return;
        }

        if (!this.inns.IsListReady())
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow,
                "エーテライトの一覧を読めません（コンテンツの中では空になります）。");
            ImGui.TextUnformatted($"いまの設定: {current}");
            return;
        }

        ImGui.SetNextItemWidth(360);

        using (var combo = ImRaii.Combo("##inn", current))
        {
            if (combo)
            {
                if (ImGui.Selectable(InnAutoLabel, this.config.InnAuto))
                {
                    this.config.InnAuto = true;
                    this.config.InnName = string.Empty;
                    this.config.Save();
                    this.log.Write("設定", "帰還先の宿屋を Lifestream に任せます");
                }

                foreach (var inn in all)
                {
                    // アクセスしていない都市の宿屋も並べる（選べないようにして理由を添える）。
                    // 一覧から消すと、なぜ出てこないのかが分からない。
                    var attuned = this.inns.IsAttuned(inn);
                    var label = attuned ? inn.Name : $"{inn.Name}（都市のエーテライトに未アクセス）";
                    var selected = !this.config.InnAuto && inn.InnTerritoryId == this.config.InnTerritoryId;

                    using (ImRaii.Disabled(!attuned))
                    {
                        if (!ImGui.Selectable($"{label}##inn{inn.InnTerritoryId}", selected))
                            continue;
                    }

                    this.config.InnAuto = false;
                    this.config.InnTerritoryId = inn.InnTerritoryId;
                    this.config.InnName = inn.Name;
                    this.config.Save();

                    this.log.Write("設定", $"帰還先を {inn.Name} にしました");
                }
            }
        }

        ImGui.TextColored(ImGuiColors.DalamudGrey,
            "宿屋の部屋の呼び鈴で回収します。宿屋までの移動（テレポート・都市内の移動・受付との会話）は Lifestream が行います。");
        ImGui.TextColored(ImGuiColors.DalamudGrey,
            "ソリューション・ナインには宿屋がありません。");

        if (this.config.InnAuto)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey,
                "Lifestream の設定「Preferred inn」→ 近くのエーテライトの都市 → "
                + "テレポ代が一番安い都市、の順で選ばれます。");
        }
        else if (this.config.InnTerritoryId == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed,
                "帰還先が未設定です。設定しないと回収へ向かえません。");
        }
        else if (this.inns.Find(this.config.InnTerritoryId) is { } chosen && !this.inns.IsAttuned(chosen))
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow,
                "選んでいる宿屋の都市のエーテライトにアクセスしていません。このままでは回収へ向かえません。");
        }

        ImGui.TextColored(ImGuiColors.DalamudGrey,
            "宿屋が未解放（開始都市の宿屋のクエストが済んでいない）だと入れません。");

        if (this.inns.Skipped.Count > 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow,
                $"都市を決められず一覧から外した宿屋: {string.Join(" / ", this.inns.Skipped)}");
        }
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// 検証（デバッグ用）：指定した宿屋へ移動できるかを、ベンチャーを待たずに確かめる。
    /// 回収と同じ部品（InnTrip・BellRunner）で動くので、ここで通れば回収のときも通る。
    /// </summary>
    private void DrawTestTab()
    {
        using var tab = ImRaii.TabItem("検証");

        if (!tab)
            return;

        ImGui.TextWrapped(
            "ほかの宿屋にも移動できるかを確かめるためのものです。"
            + "回収のときと同じ仕組み（Lifestream の宿屋機能・呼び鈴の処理）で動きます。"
            + "GatherBuddyReborn には触りません。");

        // 回収の流れが動いている間は使わせない。Lifestream と呼び鈴を取り合うため。
        var blocked = this.innTest.StartBlocked();

        if (blocked)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow,
                "GBR の自動採集が ON の間は使えません（回収の流れと取り合うため）。自動採集を OFF にしてから使ってください。");
        }

        if (!this.lifestream.IsLoaded)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, "Lifestream が見つかりません。");
            blocked = true;
        }

        var all = this.inns.All();
        var listReady = this.inns.IsListReady();

        if (!listReady)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow,
                "エーテライトの一覧を読めません（コンテンツの中では空になります）。");
        }

        ImGui.Spacing();

        using (ImRaii.Disabled(this.innTest.Running))
        {
            ImGui.Checkbox("着いたら呼び鈴を開くところまで確かめる", ref this.testOpenBell);
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(
                "回収のときと同じく、部屋の呼び鈴まで歩いて開き、AutoRetainer の処理を待ってから閉じます。\n"
                + "回収できるベンチャーがあれば、そのまま回収されます。\n"
                + "切ると、宿屋に入れたかだけを確かめます。");
        }

        // 検証中の状態と中止。
        if (this.innTest.Running)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"検証中: {this.innTest.Detail}");
            ImGui.TextUnformatted($"このあと試す宿屋: {this.innTest.Remaining} 件");

            if (ImGui.Button("検証を中止する"))
                this.innTest.Cancel("利用者が中止しました");
        }
        else
        {
            if (!string.IsNullOrEmpty(this.innTest.Detail))
                ImGui.TextColored(ImGuiColors.DalamudGrey, this.innTest.Detail);

            var attuned = listReady ? all.Where(this.inns.IsAttuned).ToList() : [];

            using (ImRaii.Disabled(blocked || attuned.Count == 0))
            {
                if (ImGui.Button($"アクセス済みの宿屋を順番にすべて検証（{attuned.Count} 件）"))
                    this.innTest.Start(attuned, this.testOpenBell);
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(
                    "上から順に1件ずつ移動します。失敗しても次の宿屋へ進みます。\n"
                    + "いま居る宿屋は最後に回します（居るままでは移動を確かめられないため）。");
            }
        }

        ImGui.Spacing();

        using (var table = ImRaii.Table("##innTest", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.Resizable))
        {
            if (table)
            {
                ImGui.TableSetupColumn("宿屋");
                ImGui.TableSetupColumn("結果");
                ImGui.TableSetupColumn("所要");
                ImGui.TableSetupColumn("暗転の隙間");
                ImGui.TableSetupColumn("操作");
                ImGui.TableHeadersRow();

                foreach (var inn in all)
                {
                    var isAttuned = listReady && this.inns.IsAttuned(inn);
                    var isCurrent = this.innTest.Current?.InnTerritoryId == inn.InnTerritoryId;

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(inn.Name);

                    if (!isAttuned)
                        ImGui.TextColored(ImGuiColors.DalamudGrey, "都市のエーテライトに未アクセス");

                    ImGui.TableNextColumn();

                    if (isCurrent)
                    {
                        ImGui.TextColored(ImGuiColors.DalamudYellow, "検証中");
                    }
                    else if (this.innTest.Results.TryGetValue(inn.InnTerritoryId, out var r))
                    {
                        var (label, color) = r.Outcome switch
                        {
                            InnTestRunner.Outcome.Success => ("成功", ImGuiColors.HealerGreen),
                            InnTestRunner.Outcome.Failed => ("失敗", ImGuiColors.DalamudRed),
                            _ => ("試していない", ImGuiColors.DalamudGrey),
                        };

                        ImGui.TextColored(color, $"{label}（{r.At:HH:mm}）");

                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(r.Detail);
                    }
                    else
                    {
                        ImGui.TextColored(ImGuiColors.DalamudGrey, "未検証");
                    }

                    this.innTest.Results.TryGetValue(inn.InnTerritoryId, out var rr);

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(rr?.Travel is { } t ? $"{t.TotalSeconds:F0} 秒" : "-");

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(rr?.SettleGap is { } g ? $"{g.TotalSeconds:F1} 秒" : "-");

                    ImGui.TableNextColumn();

                    using (ImRaii.Disabled(blocked || !isAttuned || this.innTest.Running))
                    {
                        if (ImGui.Button($"検証のため移動##test{inn.InnTerritoryId}"))
                            this.innTest.Start([inn], this.testOpenBell);
                    }
                }
            }
        }

        ImGui.TextColored(ImGuiColors.DalamudGrey,
            "結果にマウスを乗せると理由が出ます。「暗転の隙間」は、Lifestream が終わってから宿屋に入るまで何の印も立っていなかった時間です"
            + "（10 秒を超えると失敗扱いになるので、その余裕を見るためのものです）。");

        if (this.innTest.Results.Count > 0 && ImGui.Button("結果をコピー"))
            ImGui.SetClipboardText(this.ResultsText());
    }

    /// <summary>検証の結果を文字にする（報告に貼るため）。</summary>
    private string ResultsText()
    {
        var lines = new List<string> { $"GBRHelper 宿屋の検証結果（{DateTime.Now:yyyy-MM-dd HH:mm}）" };

        foreach (var inn in this.inns.All())
        {
            if (!this.innTest.Results.TryGetValue(inn.InnTerritoryId, out var r))
            {
                lines.Add($"- {inn.Name}: 未検証");
                continue;
            }

            var label = r.Outcome switch
            {
                InnTestRunner.Outcome.Success => "成功",
                InnTestRunner.Outcome.Failed => "失敗",
                _ => "試していない",
            };

            var travel = r.Travel is { } t ? $"{t.TotalSeconds:F0}秒" : "-";
            var gap = r.SettleGap is { } g ? $"{g.TotalSeconds:F1}秒" : "-";
            lines.Add($"- {inn.Name}: {label}（{r.At:HH:mm}・所要 {travel}・暗転の隙間 {gap}）{r.Detail}");
        }

        return string.Join("\n", lines);
    }

    // ------------------------------------------------------------------

    private void DrawPluginsTab()
    {
        using var tab = ImRaii.TabItem("必要なプラグイン");

        if (!tab)
            return;

        ImGui.TextWrapped("どれか1つでも欠けると、回収の流れが途中で止まります。");
        ImGui.Spacing();

        DrawRow(this.gatherBuddy, "自動採集の ON / OFF と、切り替えの通知を受け取ります");
        DrawRow(this.retainer, "ベンチャーが回収できるかを判断し、実際の回収を行います");
        DrawRow(this.lifestream, "帰還先の宿屋へ入るのに使います（テレポート・都市内の移動・受付との会話）");
        DrawRow(this.navmesh, "宿屋の部屋の中で呼び鈴まで歩くのに使います");

        this.DrawNavmeshState();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // IPC の失敗内容を画面に出す。
        // dalamud.log は 100MB で書き込みが止まり、記録が残らないことがあるため。
        ImGui.TextUnformatted("直近の通信の失敗");

        var any = false;

        foreach (var gate in new IpcGate[]
                 { this.gatherBuddy, this.retainer, this.lifestream, this.navmesh })
        {
            foreach (var (label, detail) in gate.LastErrors)
            {
                any = true;
                ImGui.TextColored(ImGuiColors.DalamudYellow, $"{gate.DisplayName} / {label}");
                ImGui.TextWrapped($"    {detail}");
            }
        }

        if (!any)
            ImGui.TextColored(ImGuiColors.DalamudGrey, "ありません。");

        return;

        static void DrawRow(IpcGate gate, string why)
        {
            var loaded = gate.IsLoaded;

            ImGui.TextColored(
                loaded ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed,
                loaded ? "導入済み" : "見つかりません");

            ImGui.SameLine();
            ImGui.TextUnformatted(gate.DisplayName);
            ImGui.TextColored(ImGuiColors.DalamudGrey, $"    {why}");
        }
    }

    /// <summary>
    /// vnavmesh のいまの状態を出す。
    ///
    /// 呼び鈴へ歩けない原因がここに集まるので、推測せずに読めるようにしておく。
    /// 特に「Auto load mesh」が切れていると、エリアを移っても
    /// ナビメッシュが読み込まれず、待っても永久に使えるようにならない。
    /// </summary>
    private void DrawNavmeshState()
    {
        if (!this.navmesh.IsLoaded)
            return;

        var ready = this.navmesh.IsReady();
        var progress = this.navmesh.BuildProgress();
        var autoLoad = this.navmesh.IsAutoLoad();

        ImGui.TextColored(ImGuiColors.DalamudGrey, "    いまのエリアのナビメッシュ:");
        ImGui.SameLine();

        if (ready)
        {
            ImGui.TextColored(ImGuiColors.HealerGreen, "使えます");
        }
        else if (progress is { } p && p >= 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"構築中（{p * 100:F0}%%）");
        }
        else
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, "読み込まれていません");
        }

        // 自動読み込みが切れていると、こちらから頼まない限り読み込まれない。
        if (autoLoad == false)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow,
                "    vnavmesh の「Auto load mesh」が切れています。");
            ImGui.TextColored(ImGuiColors.DalamudGrey,
                "    回収のときはこちらから読み込みを頼むので動きますが、"
                + "入れておくと待ち時間が短くなります。");
        }
    }

    // ------------------------------------------------------------------

    private void DrawLogTab()
    {
        using var tab = ImRaii.TabItem("記録");

        // タブ自体のクリックを数える。選ばれていないときも数えるため、if (!tab) より前で見る。
        if (ImGui.IsItemClicked() && this.logTabTaps.Tap(DateTime.UtcNow))
        {
            this.showTestTab = !this.showTestTab;
            this.log.Write("Info", this.showTestTab ? "検証タブを表示しました" : "検証タブを隠しました");
        }

        if (!tab)
            return;

        if (ImGui.Button("消去"))
            this.log.Clear();

        ImGui.SameLine();
        ImGui.TextColored(ImGuiColors.DalamudGrey,
            "ここに出ないときは /xllog も見てください。");

        ImGui.Separator();

        using var child = ImRaii.Child("##log", new Vector2(-1, -1));

        if (!child)
            return;

        foreach (var line in this.log.Snapshot())
            ImGui.TextUnformatted(line);
    }
}
