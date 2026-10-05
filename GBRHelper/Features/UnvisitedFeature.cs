using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using GBRHelper.Ipc;
using GBRHelper.Ui;

namespace GBRHelper.Features;

/// <summary>
/// 採取手帳の未採取（GBRのLog ×）を採掘／園芸×10レベル帯で登録する。
/// 2026-10-05 利用者のQ5回答に合わせ、チェックONで有効リストを作成、OFFで管理リストを削除。
/// 目標数は作成時の所持数＋1。Log「－」、伝承録未読・判定不能、別の有効リストとの重複は除外する。
/// GBR・回収・ほかの管理処理の停止を確認してから変更し、保存内容まで照合する。
/// </summary>
public sealed class UnvisitedFeature : IFeature
{
    public const string FeatureName = "ベンチャー未採取品の採取";

    /// <summary>この機能が作った GBR のリストの印（Description に入れる。名前の接頭辞だけで持ち主を決めない）。</summary>
    public const string ManagementTag = "[GBRHelper:VentureUnlock]";

    /// <summary>
    /// 前の版までの GBR のリスト名の頭（UnvisitedPlan.FormatListName に渡す）。いまは設定の記録の鍵（GatherProfiles.Name）と、
    /// 前の版のリストを見分けるのに使う。いま GBR に作るリスト名は ListNamePrefix。
    /// </summary>
    public const string ManagementTagPrefix = "GBRHelper_解放";

    /// <summary>
    /// いま GBR に作るリスト名の頭（GBRHelper_開放_園_Lv1-10_未採取）。
    /// 「解放」は、ほかのプラグインの訳の置き換えで「原初の解放」と表示されることがあるため、「開放」にした。
    /// </summary>
    public const string ListNamePrefix = "GBRHelper_開放";

    /// <summary>右ペインの列。左が採掘、右が園芸。</summary>
    private static readonly (GatherableCatalog.Job Job, string Label)[] Columns =
    [
        (GatherableCatalog.Job.Miner, "採掘"),
        (GatherableCatalog.Job.Botanist, "園芸"),
    ];

    public string Name => FeatureName;

    public string Description
        => "採取手帳で未採取（GBR の Gatherables タブで Log が「×」）の品を、Lv 帯ごとのチェックで GBR の自動採集リストへ登録します。"
         + "本人が一度採ると、その品をリテイナーのベンチャーで依頼できるようになります。";

    public int SortOrder => 100;

    /// <summary>
    /// 常に使える。ボタンを押したときだけ GBR に書く機能なので、左ペインの有効／無効は持たない
    /// （Tick はキャラ切替の検知だけで軽い）。
    /// </summary>
    public bool Enabled
    {
        get => true;
        set { }
    }

    public bool HideEnableToggle => true;

    /// <summary>右ペインの左上から「採掘」を始めるため、機能名の見出しは出さない。</summary>
    public bool ShowHeader => false;

    private readonly GatherProfileController profiles;

    private string ActiveTag => profiles.Tag(GatherProfileKind.Unlock);
    /// <summary>その帯の GBR のリストの名前（GBRHelper_開放_鉱_Lv1-10_未採取。誰のリストかは説明欄の印 ActiveTag で見る）。</summary>
    private static string OwnListName(GatherableCatalog.Job job, GatherableCatalog.LevelBand band)
        => GatherProfiles.ListName(GatherProfileKind.Unlock, GatherProfiles.Key(job, band), null);
    private readonly Configuration config;
    private readonly RunLog log;
    private readonly LiveCatalogBuilder builder;
    private readonly GatheringCompletionReader reader;
    private readonly GbrAutoGatherListAccess listAccess;
    private readonly Ipc.GatherBuddyIpc gbrIpc;

    /// <summary>ほかの自動処理で登録を止める理由（止めなくてよければ null。Plugin.BusyReason）。</summary>
    private readonly Func<string?> busyReason;

    private GatherableCatalog? catalog;
    private string catalogError = string.Empty;
    private ulong lastContentId;
    private DateTime lastReanalyzeAt = DateTime.MinValue;

    /// <summary>GBR の自動採集リストの写し（表示用）。2 秒ごと・書いた直後に読み直す。</summary>
    private IReadOnlyList<GbrAutoGatherListAccess.ListSummary>? listSnapshot;
    private DateTime nextListRefresh = DateTime.MinValue;

    // 直近の整理の結果
    private string lastWriteSummary = string.Empty;
    private bool lastWriteOk;

    public UnvisitedFeature(
        Configuration config,
        RunLog log,
        LiveCatalogBuilder builder,
        GatheringCompletionReader reader,
        GbrAutoGatherListAccess listAccess,
        Ipc.GatherBuddyIpc gbrIpc,
        Func<string?> busyReason, GatherProfileController profiles)
    {
        this.config = config;
        this.profiles = profiles;
        this.log = log;
        this.builder = builder;
        this.reader = reader;
        this.listAccess = listAccess;
        this.gbrIpc = gbrIpc;
        this.busyReason = busyReason;
    }

    /// <summary>
    /// 「ベンチャーで依頼できる品だけ」に絞る。決定「復興用なども含めない」で固定した
    /// （切り替えの設定は外した）。依頼品の一覧を読めないときは登録を停止する。
    /// </summary>
    public const bool VentureRequestOnly = true;

    private bool VentureOnly => VentureRequestOnly;

    public void ResetCharacter()
    {
        lastContentId = 0; catalog = null; listSnapshot = null;
        lastReanalyzeAt = nextListRefresh = DateTime.MinValue;
        lastWriteSummary = catalogError = ""; localPanel = null; localPanelUntil = default; lastAction = default;
    }

    public void Tick()
    {
        // キャラ変更の検知
        var cid = Svc.PlayerState.ContentId;
        if (cid != this.lastContentId)
        {
            // 古いキャラのキャッシュを捨てて計画も破棄
            if (this.lastContentId != 0)
                this.reader.InvalidateCharacter(this.lastContentId);
            this.lastContentId = cid;
            this.catalog = null; // GBR の GameData は共通なので再利用可能だが、安全側に倒す
            this.lastReanalyzeAt = DateTime.MinValue;
            this.lastWriteSummary = string.Empty;
        }

        if (DateTime.UtcNow >= this.nextCompletionCheck)
        {
            this.nextCompletionCheck = DateTime.UtcNow.AddSeconds(5);
            this.RemoveCompletedLists();
        }
    }

    private DateTime nextCompletionCheck;

    /// <summary>
    /// 対象品がなくなった Lv 帯のリストを消す（要望「対象品がなくなったら自動的にプリセット削除」）。
    /// GBR の自動採集が止まっていて、ほかの自動処理も無いときだけ、リストの作り直しを頼む（GBR は止めない。RefreshWhenStopped）。
    /// 作り直しは対象 0 件の帯のリストを消し、チェックは残す（画面ではチェック済みのまま暗くして触れなくする）。
    /// </summary>
    private void RemoveCompletedLists()
    {
        var cid = this.profiles.Character;
        if (cid == 0 || this.gbrIpc.IsAutoGatherEnabled() != false || this.busyReason() is not null)
            return;

        var selected = GatherProfiles.Effective(this.profiles.Profiles, cid, GatherProfileKind.Unlock);
        if (selected.Count == 0)
            return;

        // 解放採取の画面を一度も開いていなくても片付けられるように、品の一覧がまだ無ければ一度だけ読む（失敗しても繰り返さない）。
        if (this.catalog is null && this.lastReanalyzeAt == DateTime.MinValue)
            this.Reanalyze();
        if (this.catalog is null)
            return;

        this.RefreshListSnapshot(force: false);
        var withList = selected.Select(GatherProfiles.Decode)
            .Where(x => this.FindOwnList(OwnListName(x.Job, x.Band)) is not null)
            .ToArray();
        if (withList.Length == 0)
            return;

        // 採った品の印を読み直す（読んだ結果は覚えておく作りなので、確かめるときだけ捨てる）。
        this.reader.InvalidateCharacter(cid);
        foreach (var (job, band) in withList)
        {
            var s = UnvisitedPlan.Summarize(this.catalog, this.reader, job, band, this.VentureOnly);
            var name = OwnListName(job, band);
            if (s.Ungathered != 0 || s.Unknown != 0)
            {
                this.completionRequested.Remove(name);
                continue;
            }

            // 手で足した品が残っていると作り直してもリストは消えない。同じ帯には一度だけ頼む（記録を増やし続けない）。
            if (!this.completionRequested.Add(name))
                continue;

            this.log.Write("機能", $"{(job == GatherableCatalog.Job.Miner ? "採掘" : "園芸")} Lv{band.MinLevel}～Lv{band.MaxLevel} の対象品がなくなったので、リストを消します");
            this.profiles.RefreshWhenStopped(GatherProfileKind.Unlock);
            return;
        }
    }

    /// <summary>対象品がなくなってリストの削除を頼んだ帯（リスト名）。対象品が戻ったら外す。</summary>
    private readonly HashSet<string> completionRequested = new(StringComparer.Ordinal);

    // ------------------------------------------------------------------
    // 右ペイン

    private readonly GatherProfileTabs profileTabs = new();
    public void DrawRight() => profileTabs.Draw(profiles, GatherProfileKind.Unlock, DrawTab);

    // ------------------------------------------------------------------
    // 本人の画面の中身。描画時に件数を読み直す。

    private UnlockPanel? localPanel;
    private DateTime localPanelUntil;

    /// <summary>今のキャラクターの画面の中身（1 秒ごとに作り直す。再解析・整理のあとはすぐ作り直す）。</summary>
    public UnlockPanel LocalPanel()
    {
        if (this.localPanel is not null && DateTime.UtcNow < this.localPanelUntil)
            return this.localPanel;
        this.localPanel = this.BuildPanel();
        this.localPanelUntil = DateTime.UtcNow.AddSeconds(1);
        return this.localPanel;
    }

    private void InvalidatePanel() => this.localPanelUntil = DateTime.MinValue;

    private UnlockPanel BuildPanel()
    {
        var cid = Svc.PlayerState.ContentId;
        this.RefreshListSnapshot(force: false);
        var panel = new UnlockPanel
        {
            Problem = this.BlockReason(cid, this.gbrIpc.IsAutoGatherEnabled()),
            ResultLine = this.LocalResultLine(),
        };
        foreach (var (job, label) in Columns)
            for (var i = 0; i < 10; i++)
                panel.Bands.Add(this.BuildBand(job, label, new GatherableCatalog.LevelBand(i)));
        return panel;
    }

    /// <summary>1 つの帯の件数・登録の有無・吹き出しの行（帯にマウスを乗せたとき：押すと何が登録されるか）。</summary>
    private UnlockBand BuildBand(GatherableCatalog.Job job, string jobLabel, GatherableCatalog.LevelBand band)
    {
        var result = new UnlockBand { Key = GatherProfiles.Key(job, band) };
        var listName = OwnListName(job, band);
        if (this.FindOwnList(listName) is { } own)
            result.Registered = own.Enabled ? 1 : 2;
        if (this.catalog is null)
            return result;

        var ventureOnly = this.VentureOnly;
        var summary = UnvisitedPlan.Summarize(this.catalog, this.reader, job, band, ventureOnly);
        result.Available = true;
        result.Ungathered = summary.Ungathered;
        result.Unknown = summary.Unknown;
        result.FolkloreLocked = summary.FolkloreLocked;
        result.ExcludedNotVenture = summary.ExcludedNotVenture;

        // 説明の文章は要望で外した（見出しと対象の品の一覧だけ）。
        var tip = result.Tip;
        if (summary.Ungathered == 0 && summary.FolkloreLocked == 0)
        {
            tip.Add(new("この Lv 帯に未採取（Log ×）の品はありません", 1));
        }
        else
        {
            var conflicts = this.listSnapshot is { } lists ? UnvisitedPlan.ConflictingItemIds(lists, listName, ActiveTag) : null;
            if (summary.Ungathered == 0)
                tip.Add(new("登録できる品がありません（伝承録が要る品だけです）。", 1));
            foreach (var e in this.catalog.InBand(job, band)
                         .Where(e => !(ventureOnly && !e.VentureRequestable))
                         .Where(e => this.reader.Query(e) == GatheringCompletionReader.State.Ungathered))
            {
                var notes = new List<string>();
                if (this.catalog.VentureInfoAvailable && !e.VentureRequestable)
                    notes.Add("ベンチャー不可");
                switch (this.reader.FolkloreOk(e))
                {
                    case false:
                        notes.Add($"「{string.Join("」「", e.FolkloreBooks!.Select(this.BookName))}」を読んでいないため登録しない");
                        break;
                    case null:
                        notes.Add("伝承録を読んだか確かめられないため登録しない");
                        break;
                }
                if (conflicts?.Contains(e.ItemId) == true)
                    notes.Add("ほかの有効リストにあるため登録しない");

                var line = $"  Lv{e.Level}　{e.Name}" + (notes.Count > 0 ? $"（{string.Join("・", notes)}）" : string.Empty);
                tip.Add(new(line, notes.Count > 0 ? 1 : 0));
            }
        }

        if (summary.ExcludedNotVenture > 0)
            tip.Add(new($"ベンチャーで依頼できない品 {summary.ExcludedNotVenture} 件は、設定で外しています", 1));
        if (summary.Unknown > 0)
            tip.Add(new($"採取履歴を読めなかった品 {summary.Unknown} 件は登録しません（「再解析」で読み直します）", 2));
        return result;
    }

    /// <summary>いまチェックを変えられない理由（件数を出せない理由）。変えられるなら null。</summary>
    private string? BlockReason(ulong cid, bool? gbrEnabled)
    {
        if (cid == 0)
            return "ログインしていません";
        if (gbrEnabled is null)
            return "GatherBuddyReborn の状態を読めません（読み込まれていない可能性があります）";
        if (this.catalog is null)
            return "まだ解析していません（下の「再解析」を押してください）";
        if (this.VentureOnly && !this.catalog.VentureInfoAvailable)
            return "ベンチャーの依頼品を読めません。再解析してください";
        return null;
    }

    /// <summary>今のキャラクターの「再解析」「完了分を整理」の結果の 1 行。</summary>
    private string LocalResultLine() => this.lastAction switch
    {
        LastAction.Reanalyze => this.catalogError.Length != 0 ? this.catalogError : $"採取手帳の記録を読み直しました（{this.lastReanalyzeAt:HH:mm:ss}）",
        LastAction.Clean => this.lastWriteSummary,
        _ => this.catalogError,
    };

    /// <summary>タブ 1 つ（cid のキャラクター）。今のキャラクターならこのゲームの中身、ほかならそのゲームから届いた中身で描く。</summary>
    private void DrawTab(ulong cid)
    {
        if (cid == profiles.Character)
            this.DrawLocal(cid);

    }

    private void DrawLocal(ulong cid)
    {
        if (catalog is null && lastReanalyzeAt == DateTime.MinValue) Reanalyze();
        var gbrEnabled = this.gbrIpc.IsAutoGatherEnabled();
        var busy = this.busyReason();
        var panel = this.LocalPanel();

        // チェックは保存するだけ（GBR には「Auto-Gatherに追加」で反映する）なので、GBR の採集中・ほかの自動処理中でも入れられる
        // （指摘「採取中に設定が出来ないのは厳しい」）。
        var checkBlock = panel.Problem;
        // 「Auto-Gatherに追加」：GBR の自動採集中は押せない（橙色「採取中につき操作を受け付けられません」）。
        // ほかの自動処理（ベンチャー回収・霊砂など）の間も押せない。
        var applyBlock = checkBlock ?? (gbrEnabled == true ? GatherProfileController.AutoGatheringText : busy);
        // 「完了分を整理」：リストを書き換えるので、GBR が止まっていて、ほかの自動処理も無いときだけ。
        var cleanBlock = checkBlock ?? (gbrEnabled == true ? GatherProfileController.BusyText : busy);

        this.DrawGrid(panel, checkBlock, null, requireData: true);

        ImGui.Spacing();
        ImGui.Separator();
        this.DrawButtons(
            applyBlock, () => { profiles.Apply(GatherProfileKind.Unlock); this.lastAction = LastAction.Apply; },
            cid == 0 ? "ログインしていません" : null, () => { this.Reanalyze(); this.lastAction = LastAction.Reanalyze; },
            cleanBlock, () => { this.CleanCompleted(); this.lastAction = LastAction.Clean; },
            profiles.StopBlock?.Invoke(), () => { profiles.Stop(GatherProfileKind.Unlock); this.lastAction = LastAction.Stop; });

        // 結果は 1 行だけ。「Auto-Gatherに追加」「Auto-Gatherに停止」の結果は、ほかのボタンを押すまで出し続ける。
        var result = this.lastAction is LastAction.Reanalyze or LastAction.Clean ? this.LocalResultLine()
            : this.catalogError.Length != 0 ? this.catalogError : profiles.StatusOf(GatherProfileKind.Unlock);
        if (!string.IsNullOrEmpty(result))
            ImGui.TextWrapped(result);
    }





    /// <summary>
    /// 帯のチェックの表（左が採掘、右が園芸）。requireData：件数が無い帯はチェックできない（今のキャラクター）。
    /// </summary>
    private void DrawGrid(UnlockPanel? panel, string? checkBlock, string? noData, bool requireData)
    {
        var gap = ImGui.GetTextLineHeight() * 0.5f; // 「少し空間を開けて」＝文字の高さの半分

        using var table = ImRaii.Table("##unlockGrid", Columns.Length, ImGuiTableFlags.SizingStretchSame);
        if (!table)
            return;

        // 見出し：左上「採掘」・右上「園芸」
        ImGui.TableNextRow();
        for (var col = 0; col < Columns.Length; col++)
        {
            ImGui.TableSetColumnIndex(col);
            ImGui.TextUnformatted(Columns[col].Label);
        }

        for (var i = 0; i < 10; i++)
        {
            ImGui.TableNextRow();
            for (var col = 0; col < Columns.Length; col++)
            {
                ImGui.TableSetColumnIndex(col);
                ImGui.Dummy(new Vector2(0, gap));
                var (job, label) = Columns[col];
                var band = new GatherableCatalog.LevelBand(i);
                var key = GatherProfiles.Key(job, band);
                this.DrawBand(job, label, band, panel?.Bands.FirstOrDefault(b => b.Key == key), checkBlock, noData, requireData);
            }
        }
    }

    private void DrawBand(GatherableCatalog.Job job, string jobLabel, GatherableCatalog.LevelBand band, UnlockBand? data,
        string? checkBlock, string? noData, bool requireData)
    {
        var selected = profiles.Selected(GatherProfileKind.Unlock, job, band);
        var available = data is { Available: true };

        // 対象品がなくなった帯は、チェックが付いていても暗くして触れなくする（リストは RemoveCompletedLists で消す）。
        var completed = data is { Completed: true };
        bool clicked;
        using (ImRaii.Disabled(checkBlock is not null || (requireData && !available) || completed))
            clicked = ImGui.Checkbox($"Lv{band.MinLevel}～Lv{band.MaxLevel}##unlock{job}{band.SegmentIndex}", ref selected);

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.BeginTooltip();
            try
            {
                ImGui.TextUnformatted($"{jobLabel} Lv{band.MinLevel}～Lv{band.MaxLevel}");
                if (checkBlock is not null)
                    ImGui.TextColored(ImGuiColors.DalamudYellow, checkBlock);
                if (!available && noData is not null)
                    ImGui.TextColored(ImGuiColors.DalamudGrey, noData);
                foreach (var line in data?.Tip ?? [])
                {
                    if (line.Tone == 0) ImGui.TextUnformatted(line.Text);
                    else ImGui.TextColored(line.Tone == 2 ? ImGuiColors.DalamudYellow : ImGuiColors.DalamudGrey, line.Text);
                }
            }
            finally
            {
                ImGui.EndTooltip();
            }
        }

        if (clicked)
            profiles.Set(GatherProfileKind.Unlock, job, band, selected);

        // チェックの右：件数と登録の有無
        ImGui.SameLine();
        if (!available)
            ImGui.TextColored(ImGuiColors.DalamudGrey, "－");
        else if (data!.Ungathered > 0)
            ImGui.TextUnformatted($"未採取 {data.Ungathered}件");
        else if (data.Unknown == 0)
            ImGui.TextColored(selected ? ImGuiColors.HealerGreen : ImGuiColors.DalamudGrey, selected ? "採取完了" : "未採取なし");

        if (data is { Unknown: > 0 })
        {
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"判定待ち {data.Unknown}件");
        }

        if (data is { FolkloreLocked: > 0 })
        {
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudGrey, $"伝承録待ち {data.FolkloreLocked}件");
        }

        if (data is { Registered: > 0 })
        {
            ImGui.SameLine();
            if (data.Registered == 1)
                ImGui.TextColored(ImGuiColors.HealerGreen, "登録済み");
            else
                ImGui.TextColored(ImGuiColors.DalamudYellow, "登録済み（GBR で無効）");
        }
    }

    /// <summary>最後に押したボタン（下の 1 行に、その結果を出すため）。</summary>
    private enum LastAction { None, Apply, Reanalyze, Clean, Stop }

    private LastAction lastAction;

    /// <summary>
    /// 下のボタン（要望：説明の文は消し、「Auto-Gatherに追加」「再解析」「完了分を整理」だけ。
    /// 同日の依頼で、「Auto-Gatherに追加」の下に「Auto-Gatherに停止」を足した）。押せないときは理由を一番上に出す。
    /// </summary>
    private void DrawButtons(string? applyBlock, Action apply, string? reanalyzeBlock, Action reanalyze,
        string? cleanBlock, Action clean, string? stopBlock, Action stop)
    {
        using (ImRaii.Disabled(applyBlock is not null))
        {
            if (ImGui.Button("Auto-Gatherに追加##unlockApply"))
                apply(); // この機能のリストだけを反映する（全素材の補充のリストには触らない）
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ButtonTooltip(applyBlock, ApplyTooltip);

        ImGui.SameLine();
        using (ImRaii.Disabled(reanalyzeBlock is not null))
        {
            if (ImGui.Button("再解析（採取履歴を読み直す）"))
                reanalyze();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ButtonTooltip(reanalyzeBlock, "採取手帳の記録を読み直します。\n採った品や読んだ伝承録が、件数に出ていないときに押します。");

        ImGui.SameLine();
        using (ImRaii.Disabled(cleanBlock is not null))
        {
            if (ImGui.Button("完了分を整理"))
                clean();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ButtonTooltip(cleanBlock, "採り終えた品をリストから外します。\n採取が進んだあと、GBR を止めて押します。");

        // 「Auto-Gatherに追加」の下。
        using (ImRaii.Disabled(stopBlock is not null))
        {
            if (ImGui.Button("Auto-Gatherに停止##unlockStop"))
                stop();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ButtonTooltip(stopBlock, StopTooltip);
    }

    /// <summary>「Auto-Gatherに停止」の説明（解放採取・全素材の補充で同じ）。</summary>
    internal const string StopTooltip = "GBR の自動採集を止めます（ベンチャー回収の見張りも止めます）。";

    /// <summary>解放採取の「Auto-Gatherに追加」の説明（全素材の補充のボタンとは別物。この機能のリストだけを変える）。</summary>
    internal const string ApplyTooltip =
        "チェックした Lv 帯の未採取の品を、GBR の Auto-Gather に追加します（外した帯のリストは消します）。";

    /// <summary>ボタンの説明。押せないときは、その理由を一番上に出す（既定は黄色。reasonColor で色を変える）。</summary>
    internal static void ButtonTooltip(string? blockReason, string text, System.Numerics.Vector4? reasonColor = null)
    {
        ImGui.BeginTooltip();
        try
        {
            // 自動採集中の文は橙色、ほかの理由は黄色。
            if (blockReason is not null)
                ImGui.TextColored(reasonColor ?? (blockReason == GatherProfileController.AutoGatheringText ? ImGuiColors.DalamudOrange : ImGuiColors.DalamudYellow), blockReason);
            ImGui.TextUnformatted(text);
        }
        finally
        {
            ImGui.EndTooltip();
        }
    }

    private readonly Dictionary<uint, string> bookNames = new();

    /// <summary>伝承録の品番 → 日本語名（ゲームデータから引いて覚える）。</summary>
    private string BookName(uint bookItemId)
    {
        if (this.bookNames.TryGetValue(bookItemId, out var name))
            return name;

        try
        {
            name = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(bookItemId, out var row)
                ? row.Name.ExtractText()
                : string.Empty;
        }
        catch
        {
            name = string.Empty;
        }

        if (string.IsNullOrEmpty(name))
            name = $"伝承録 {bookItemId}";
        this.bookNames[bookItemId] = name;
        return name;
    }

    /// <summary>この機能が作った、指定の名前のリスト（写しから探す）。無ければ null。</summary>
    private GbrAutoGatherListAccess.ListSummary? FindOwnList(string listName)
        => this.listSnapshot?.FirstOrDefault(l => l.Name == listName && l.Description.Contains(ActiveTag, StringComparison.Ordinal));

    private void RefreshListSnapshot(bool force)
    {
        if (!force && DateTime.UtcNow < this.nextListRefresh)
            return;

        // 表示の更新間隔（次へ進むための待ちではない）。毎フレーム反射で全リストを読まないため。
        this.nextListRefresh = DateTime.UtcNow.AddSeconds(2);
        this.listSnapshot = this.listAccess.ListAll();
    }

    // ------------------------------------------------------------------
    // 再解析

    private void Reanalyze()
    {
        try
        {
            // キャッシュを捨ててから作り直す
            this.reader.InvalidateAll();
            this.catalog = this.builder.Build();
            this.catalogError = string.IsNullOrEmpty(this.builder.LastError) ? string.Empty : this.builder.LastError;
            this.lastReanalyzeAt = DateTime.Now;
            this.RefreshListSnapshot(force: true);
            this.InvalidatePanel();

            if (this.catalog is null)
            {
                this.log.Write("機能", $"再解析失敗: {this.catalogError}");
                return;
            }

            this.log.Write("機能",
                $"再解析完了: 対象 {this.catalog.Entries.Length} 種（警告 {this.catalog.Diag.Warnings.Count} 件）");
        }
        catch (Exception ex)
        {
            this.catalogError = $"再解析で例外: {ex.GetType().Name}: {ex.Message}";
            Svc.Log.Error($"[GBRHelper] 再解析で例外: {ex}");
        }
    }

    // ------------------------------------------------------------------
    private void CleanCompleted()
    {
        // Gathered になった行だけを除く：当該リストを読み、全行 Gathered なら削除、
        // 部分的に Gathered なら「新しい内容で WriteManagedList して置き換え」。
        if (this.catalog is null) return;
        var cid = Svc.PlayerState.ContentId;
        if (!this.CanWrite(cid)) return;
        this.reader.InvalidateCharacter(cid);

        var all = this.listAccess.ListAll();
        if (all is null)
        {
            this.ReportWriteFailure("整理：GBR の自動採集リストを読めません: " + this.listAccess.LastError);
            return;
        }

        var changed = 0;
        var removed = 0;
        var failures = new List<string>();

        foreach (var sum in all)
        {
            if (!this.CanWrite(cid)) return;
            if (!sum.Description.Contains(ActiveTag, StringComparison.Ordinal))
                continue;

            var keep = new List<(uint ItemId, uint Quantity)>();
            foreach (var (id, q) in sum.Entries)
            {
                // GatheringItemId を得るため、カタログで引く
                var e = this.catalog.Entries.FirstOrDefault(x => x.ItemId == id);
                if (e is null)
                {
                    // カタログに無いアイテム（Helper 外から追加された可能性）→ 保持
                    keep.Add((id, q));
                    continue;
                }

                // 採取済みだけ消す。Unknown（読めない）は完了扱いしない
                if (this.reader.Query(e) == GatheringCompletionReader.State.Gathered)
                    continue;
                keep.Add((id, q));
            }

            if (keep.Count == sum.Entries.Count)
                continue; // 変化なし

            if (keep.Count == 0)
            {
                if (this.listAccess.RemoveManagedList(sum.Name, ActiveTag))
                    removed++;
                else
                    failures.Add($"{sum.Name}: {this.listAccess.LastError}");
            }
            else
            {
                var result = this.listAccess.WriteManagedList(sum.Name, ActiveTag, keep, enabled: sum.Enabled);
                if (result.Ok)
                    changed++;
                else
                    failures.Add($"{sum.Name}: {result.Error}");
            }
        }

        this.RefreshListSnapshot(force: true);
        this.InvalidatePanel();
        this.lastWriteOk = failures.Count == 0;
        this.lastWriteSummary = $"整理：更新 {changed} リスト / 削除 {removed} リスト"
            + (failures.Count > 0 ? $"。失敗：{string.Join(" / ", failures)}" : string.Empty);
        this.log.Write("機能", this.lastWriteSummary);
    }

    private bool CanWrite(ulong expectedContentId)
    {
        if (expectedContentId != 0 && Svc.PlayerState.ContentId == expectedContentId
            && Svc.Framework.IsInFrameworkUpdateThread
            && this.gbrIpc.IsAutoGatherEnabled() == false && this.busyReason() is null)
            return true;

        this.ReportWriteFailure(this.busyReason() ?? "キャラクターと停止状態を確認できません。GBR の自動採集を止めてから押してください");
        return false;
    }

    private void ReportWriteFailure(string reason)
    {
        this.lastWriteOk = false;
        this.lastWriteSummary = "登録しませんでした：" + reason;
        this.log.Write("機能", this.lastWriteSummary);
    }
}
