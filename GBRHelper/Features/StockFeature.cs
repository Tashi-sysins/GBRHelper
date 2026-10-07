using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using GBRHelper.Ipc;
using GBRHelper.Ui;

namespace GBRHelper.Features;

public sealed class StockFeature(LiveCatalogBuilder builder, GatheringCompletionReader reader,
    GbrAutoGatherListAccess lists, GatherBuddyIpc gbr, GbrTimedAccess access, Func<string?> busyReason, GatherProfileController profiles,
    RunLog log) : IFeature
{
    private bool busy() => busyReason() is not null;

    public string Name => "全素材の補充";
    /// <summary>右ペインの先頭の説明（指定の文）。</summary>
    public string Description => "チェックONで、Lv帯の各品を右欄の個数（初期値100個）を採取するプリセットを作成し、GBRのAuto-Gatherに追加します。";
    public int SortOrder => 110;
    public bool Enabled { get => true; set { } }
    public bool HideEnableToggle => true;
    private readonly MaterialInventory inventory = new(lists);
    private GatherableCatalog? catalog;
    private Dictionary<uint, int>? counts;
    private IReadOnlyList<GbrAutoGatherListAccess.ListSummary>? snapshot;
    private ulong character;
    private DateTime refresh;
    private DateTime nextGuard;
    private string inventoryFailure = "";
    private string status = "";

    /// <summary>今のキャラクターの画面の中身（描画時に Refresh で作り直す）。</summary>
    private StockPanel? localPanel;

    public void ResetCharacter()
    {
        character = 0; catalog = null; counts = null; snapshot = null; refresh = default;
        localPanel = null; inventoryFailure = status = ""; ownListNames.Clear(); retainerCounts.Clear();
    }

    public void Tick()
    {
        if (character != Svc.PlayerState.ContentId)
        {
            character = Svc.PlayerState.ContentId;
            catalog = null; counts = null; snapshot = null; refresh = default; localPanel = null;
            inventoryFailure = ""; ownListNames = new(); retainerCounts.Clear();
        }

        // 吹き出しで乗せている品のリテイナーの数を読む（画面の表示だけ）。
        retainerCounts.Tick();

        // 希望所持数の帯の GBR のリストを、いまのリテイナーの数に合わせて保つ（GBR の自動採集が止まっているときだけ。2026-10-07）。
        if (DateTime.UtcNow >= nextDesiredCheck)
        {
            nextDesiredCheck = DateTime.UtcNow.AddSeconds(5);
            SyncDesiredLists();
        }

        // リテイナーの在庫の見張りは、リテイナーも数えるときだけ（いまは数えない。GatherProfiles.StockUsesRetainers）。
        if (!GatherProfiles.StockUsesRetainers) return;
        if (character == 0 || busy() || gbr.IsAutoGatherEnabled() != true) return;
        if (inventoryFailure.Length != 0)
        {
            // GBRは在庫連携が切れると本人分だけへ戻る。誤って補充しないよう切れ目で止める。
            if (access.SafeBoundary && gbr.SetAutoGatherEnabled(false))
            { status = $"在庫を確認できないためGBRを停止しました：{inventoryFailure}"; inventoryFailure = ""; }
            return;
        }
        if (DateTime.UtcNow < nextGuard) return;
        nextGuard = DateTime.UtcNow.AddSeconds(2);
        var active = lists.ListAll()?.Where(x => x.Enabled && x.Description.Contains(Tag, StringComparison.Ordinal)).ToArray();
        if (active is { Length: > 0 } && inventory.Read([], true) is null)
            inventoryFailure = inventory.Error;
    }

    private string Tag => profiles.Tag(GatherProfileKind.Stock);

    private DateTime nextDesiredCheck;

    // 見張り（FindDesiredChange）が鞄の数・レベル・リテイナーの数を読む口。リストを作るとき（GatherProfileController.Build）と同じ読み方。
    // 試験ではリフレクションで偽物に差し替える（ゲームの鞄・Allagan Tools が無いため）。
    private Func<uint, int?> bagCount = MaterialInventory.Local;
    private Func<GatherableCatalog.Job, int> levelOf = MaterialInventory.Level;
    private Func<Dictionary<uint, int>> readRetainers = AllaganRetainerCounter.ReadAll;

    /// <summary>
    /// 希望所持数の帯の GBR のリストが、いまのリテイナーの数・鞄の数とずれていたら作り直しを頼む（判断は DesiredStockSync）。
    /// GBR の自動採集が止まっていて、ほかの自動処理も無く、ほかの書き直しも待っていないときだけ（GBR は止めない。RefreshWhenStopped）。
    /// リテイナーへ預けた・引き出したあと、利用者が「Auto-Gatherに追加」を押し直さなくても数が合うようにするため。
    /// ベンチャー回収から戻るときは ResumeWait が同じことをする。
    /// </summary>
    private void SyncDesiredLists()
    {
        if (profiles.Character == 0 || gbr.IsAutoGatherEnabled() != false || busyReason() is not null || profiles.Pending)
            return;
        if (FindDesiredChange() is not { } found)
            return;
        log.Write("機能", $"全素材の補充：{Describe(found.Key, found.Change)}ので、希望所持数のリストを作り直します");
        profiles.RefreshWhenStopped(GatherProfileKind.Stock);
    }

    /// <summary>ベンチャー回収から戻る前に、希望所持数のリストを作り直しているときの文（RelayController の Detail に出る）。</summary>
    public const string ResumeWaitText = "全素材の補充のリストを、リテイナーの数に合わせています";

    /// <summary>
    /// ベンチャー回収から自動採集へ戻す前に待つ理由（待たなくてよければ null。RelayController.ResumeBlocked）。
    /// 回収でリテイナーの数が変わることがある（ベンチャーの戦利品はリテイナーの持ち物に入る・AutoRetainer の預け入れで鞄から移る）。
    /// 希望所持数のリストが古いまま GBR を戻すと、鞄から移った分まで採り直すので、作り直してから戻す。
    /// 読めない（Allagan Tools が準備中など）・作り直しを頼めないときは待たない（そのまま戻し、次に GBR が止まったときに SyncDesiredLists で合わせる）。
    /// </summary>
    public string? ResumeWait()
    {
        if (profiles.Character == 0 || !HasDesiredBands())
            return null;
        if (profiles.PendingFor(GatherProfileKind.Stock))
            return ResumeWaitText;
        if (FindDesiredChange() is not { } found)
            return null;
        profiles.RefreshWhenStopped(GatherProfileKind.Stock);
        if (!profiles.PendingFor(GatherProfileKind.Stock))
            return null;
        log.Write("機能", $"全素材の補充：{Describe(found.Key, found.Change)}ので、希望所持数のリストを作り直してから自動採集へ戻します");
        return ResumeWaitText;
    }

    /// <summary>今のキャラクターに、GBR に反映した希望所持数の帯があるか。</summary>
    private bool HasDesiredBands()
        => DesiredKeys(profiles.Profiles.GetValueOrDefault(profiles.Character)).Length > 0;

    /// <summary>GBR に反映した帯のうち、希望所持数の帯（反映した時点の設定で見る。GatherProfile.AppliedDesired）。</summary>
    private int[] DesiredKeys(GatherProfile? profile)
        => profile is null ? [] : GatherProfiles.Effective(profiles.Profiles, profiles.Character, GatherProfileKind.Stock)
            .Where(profile.AppliedDesired).Order().ToArray();

    /// <summary>
    /// 希望所持数の帯で、リストを作り直すきっかけ（最初に見つかった帯と品）。作り直さなくてよければ null。
    /// リテイナーの数・鞄の数・品の一覧を読めないときも null（判断しない。推測で作り直さない）。
    /// 品の選び方・鞄の数え方はリストを作るとき（GatherProfileController.Build）と同じ。
    /// </summary>
    private (int Key, DesiredStockSync.Change Change)? FindDesiredChange()
    {
        var profile = profiles.Profiles.GetValueOrDefault(profiles.Character);
        var keys = DesiredKeys(profile);
        if (profile is null || keys.Length == 0)
            return null;
        catalog ??= builder.Build();
        if (catalog is null)
            return null;
        Dictionary<uint, int> retainers;
        try { retainers = readRetainers(); }
        catch { return null; }

        foreach (var key in keys)
        {
            var (job, band) = GatherProfiles.Decode(key);
            var level = levelOf(job);
            var ids = catalog.InBand(job, band).Where(e => MaterialPlan.CanStock(e, level, reader.FolkloreOk)).Select(e => e.ItemId).Distinct().ToArray();
            var bag = new Dictionary<uint, int>();
            foreach (var id in ids)
            {
                if (bagCount(id) is not { } n) return null;
                bag[id] = n;
            }
            var now = DesiredStockSync.Measure(ids, bag, retainers, profile.AppliedQuantity(key));
            if (DesiredStockSync.Find(profiles.DesiredBasis(key), now) is { } change)
                return (key, change);
        }
        return null;
    }

    /// <summary>作り直すきっかけの説明（記録に出す）。</summary>
    private string Describe(int key, DesiredStockSync.Change change)
    {
        var (job, band) = GatherProfiles.Decode(key);
        var label = $"{(job == GatherableCatalog.Job.Miner ? "採掘" : "園芸")} Lv{band.MinLevel}～Lv{band.MaxLevel}";
        var name = catalog?.InBand(job, band).FirstOrDefault(e => e.ItemId == change.ItemId)?.Name ?? $"品 {change.ItemId}";
        return change.Reason switch
        {
            DesiredStockSync.Reason.RetainerChanged => $"{label} の {name} のリテイナーの数が {change.Before} → {change.After} 個に変わった",
            DesiredStockSync.Reason.Shortage => $"{label} の {name} が鞄で減り、希望所持数に足りなくなった",
            _ => $"{label} のリストを作ったときの数を覚えていない（ログインし直した・プラグインを読み直した）",
        };
    }

    /// <summary>
    /// その帯の、いま GBR にあるこのキャラクターの補充のリストの名前（無ければ空）。
    /// 名前（GBRHelper_100_鉱_Lv1-10）は数で変わるので、説明欄の印で見分けて覚えておく（Refresh で作り直す）。
    /// </summary>
    private string ListName(GatherableCatalog.Job job, GatherableCatalog.LevelBand band)
        => ownListNames.GetValueOrDefault(GatherProfiles.Key(job, band), "");

    private Dictionary<int, string> ownListNames = new();

    /// <summary>「希望所持数」の吹き出しに出すリテイナーの数（乗せた品だけ 10 秒に 1 回、Allagan Tools から読む）。</summary>
    private readonly AllaganRetainerCounter retainerCounts = new();

    private void Refresh()
    {
        refresh = DateTime.UtcNow.AddSeconds(2);
        catalog ??= builder.Build();
        snapshot = lists.ListAll();
        var owned = snapshot is null ? [] : snapshot
            .Select(x => (List: x, Owned: GatherProfiles.Identify(x)))
            .Where(x => x.Owned is { Kind: GatherProfileKind.Stock } o && o.Character == character)
            .ToArray();
        ownListNames = owned.GroupBy(x => x.Owned!.Key).ToDictionary(g => g.Key, g => g.First().List.Name);
        var levels = new Dictionary<GatherableCatalog.Job, int>
        {
            [GatherableCatalog.Job.Miner] = MaterialInventory.Level(GatherableCatalog.Job.Miner),
            [GatherableCatalog.Job.Botanist] = MaterialInventory.Level(GatherableCatalog.Job.Botanist),
        };
        counts = catalog is null ? null : inventory.Read(catalog.Entries
            .Where(e => MaterialPlan.CanStock(e, levels[e.Job], reader.FolkloreOk)).Select(e => e.ItemId), GatherProfiles.StockUsesRetainers);

        // 画面の中身：帯ごとの採る候補の品（目標は、画面の数で決める。MaterialPlan.FromCandidates）。
        var panel = new StockPanel { Problem = Block() ?? (catalog is null ? builder.LastError : counts is null ? inventory.Error : null) };
        var profile = character == 0 ? null : profiles.Get(character);
        for (var col = 0; col < 2; col++)
            for (var i = 0; i < 10; i++)
            {
                var job = (GatherableCatalog.Job)col;
                var band = new GatherableCatalog.LevelBand(i);
                var key = GatherProfiles.Key(job, band);
                var data = new StockBand { Key = key };
                if (owned.FirstOrDefault(x => x.Owned!.Key == key) is { List: { } own })
                    data.Registered = own.Enabled ? 1 : 2;
                // 希望所持数の帯は、決めた目標を使わない（リストは毎回「数 − いまのリテイナーの数」で作るので、候補も目標なしで出す。2026-10-07）。
                var fixedTargets = profile is null || profile.AppliedDesired(key) || profile.StockDesiredTotal.Contains(key)
                    ? null : profile.StockTargets.GetValueOrDefault(key);
                if (catalog is not null && counts is not null && snapshot is not null)
                    data.Items = MaterialPlan.Candidates(catalog.InBand(job, band), levels[job], reader.FolkloreOk, counts,
                        UnvisitedPlan.ConflictingItemIds(snapshot, ListName(job, band), Tag), fixedTargets);
                panel.Bands.Add(data);
            }
        localPanel = panel;
    }

    /// <summary>
    /// チェックを変えられない理由。チェックは保存するだけ（GBR には「Auto-Gatherに追加」で反映する）なので、
    /// GBR の採集中・ほかの自動処理中でも変えられる（指摘「採取中に設定が出来ないのは厳しい」）。
    /// </summary>
    private string? Block()
        => character == 0 ? "ログインしてください" : gbr.IsAutoGatherEnabled() is null
            ? "GatherBuddyReborn の状態を読めません（読み込まれていない可能性があります）" : null;

    /// <summary>その帯の行（画面の数で目標を決める）。中身が無ければ空。</summary>
    private List<MaterialPlan.Row> Rows(StockPanel? panel, GatherableCatalog.Job job, GatherableCatalog.LevelBand band)
    {
        var key = GatherProfiles.Key(job, band);
        return panel?.Problem is null && panel?.Bands.FirstOrDefault(b => b.Key == key) is { } data
            ? MaterialPlan.FromCandidates(data.Items, profiles.Quantity(job, band))
            : [];
    }

    private static bool HasData(StockPanel? panel) => panel is { Problem: null };

    private readonly GatherProfileTabs profileTabs = new();
    public void DrawRight() => profileTabs.Draw(profiles, GatherProfileKind.Stock, DrawTab);

    /// <summary>
    /// 1 つの帯のチェックを選べるか。中身（件数）があれば、品がある帯・チェック済みの帯だけ。
    /// 中身が無いとき、requireData なら選べない。
    /// </summary>
    private bool Selectable(StockPanel? panel, bool requireData, GatherableCatalog.Job job, GatherableCatalog.LevelBand band)
        => profiles.Selected(GatherProfileKind.Stock, job, band)
           || (HasData(panel) ? Rows(panel, job, band).Count > 0 : !requireData);

    /// <summary>
    /// 一列（採掘または園芸）の一括チェック 2 つ。
    /// 「全Lv帯」：選べる帯（対象の品がある帯・登録済みの帯）を全部入れる／全部外す。
    /// 「希望所持数（一括）」：チェックが入っていない帯の「希望所持数」を全部入れる／全部外す（チェックが入っている帯は変えない）。
    /// </summary>
    private void DrawBulk(GatherableCatalog.Job job, string? block, StockPanel? panel, bool requireData)
    {
        var bands = Enumerable.Range(0, 10).Select(i => new GatherableCatalog.LevelBand(i)).ToArray();
        var selectable = bands.Where(b => Selectable(panel, requireData, job, b)).ToArray();
        var allOn = selectable.Length > 0 && selectable.All(b => profiles.Selected(GatherProfileKind.Stock, job, b));
        using (ImRaii.Disabled(block is not null || selectable.Length == 0))
        {
            if (ImGui.Checkbox($"全Lv帯##bulk{job}", ref allOn))
            {
                foreach (var b in selectable)
                {
                    var on = profiles.Selected(GatherProfileKind.Stock, job, b);
                    if (allOn && !on)
                    {
                        profileTabs.Quantities.CommitPending(profiles, job, b);
                        profiles.Set(GatherProfileKind.Stock, job, b, true);
                    }
                    else if (!allOn && on)
                        profiles.Set(GatherProfileKind.Stock, job, b, false);
                }
            }
        }

        // 下の各 Lv 帯の「希望所持数」のチェックの真上に置く。
        ImGui.SameLine(StockQuantityEditor.Columns().Desired);
        var unlocked = bands.Where(b => !profiles.Selected(GatherProfileKind.Stock, job, b)).ToArray();
        var allDesired = unlocked.Length > 0 && unlocked.All(b => profiles.DesiredTotal(job, b));
        using (ImRaii.Disabled(block is not null || unlocked.Length == 0))
        {
            if (ImGui.Checkbox($"希望所持数（一括）##bulkDesired{job}", ref allDesired))
                foreach (var b in unlocked)
                    profiles.SetDesiredTotal(job, b, allDesired);
        }
    }

    /// <summary>ログイン中のキャラクターだけ描く。</summary>
    private void DrawTab(ulong cid)
    {
        if (cid == profiles.Character)
            DrawLocal();

    }

    private void DrawLocal()
    {
        if (character != Svc.PlayerState.ContentId) Tick();
        if (DateTime.UtcNow >= refresh) Refresh();
        var block = Block();
        DrawGrid(localPanel, block, null, true, id => retainerCounts.TryGet(id, out var n) ? (true, n, retainerCounts.Error) : (false, null, ""));

        ImGui.Spacing();
        ImGui.Separator();

        // チェックした帯を GBR に反映するボタン。
        // 解放採取の「Auto-Gatherに追加」とは別物：この機能のリストだけを変え、解放採取のリストには触らない。
        // GBR の自動採集中は押せない（橙色「採取中につき操作を受け付けられません」）。チェックは自動採集中も変えられる。
        var applyBlock = block ?? (gbr.IsAutoGatherEnabled() == true ? GatherProfileController.AutoGatheringText : busyReason());
        DrawButtons(applyBlock, () => profiles.Apply(GatherProfileKind.Stock),
            block, () => { catalog = null; reader.InvalidateCharacter(character); Refresh(); },
            profiles.StopBlock?.Invoke(), () => profiles.Stop(GatherProfileKind.Stock));

        // 説明の文は出さない（要望「読む気もしない」に合わせ、止まっている理由と読めない理由・反映の結果だけ出す）。
        var problem = block ?? localPanel?.Problem ?? "";
        if (problem.Length != 0)
            ImGui.TextWrapped(problem);
        if (status.Length != 0)
            ImGui.TextWrapped(status);
        ImGui.TextWrapped(profiles.StatusOf(GatherProfileKind.Stock));
    }




    /// <summary>
    /// 帯のチェックの表（左が採掘、右が園芸）。retainerOf は品のリテイナーの数（読めたか・数・読めない理由）。
    /// requireData：件数が無い帯はチェックできない（今のキャラクター）。
    /// </summary>
    private void DrawGrid(StockPanel? panel, string? block, string? noData, bool requireData,
        Func<uint, (bool Known, int? Count, string Error)> retainerOf)
    {
        // 冒頭の説明文 2 行は要望で外した（「全部要らない」）。
        using var table = ImRaii.Table("stockGrid", 2, ImGuiTableFlags.SizingStretchSame);
        if (!table)
            return;

        ImGui.TableSetupColumn("採掘"); ImGui.TableSetupColumn("園芸"); ImGui.TableHeadersRow();

        // Lv帯の一括チェックと、希望所持数の一括チェック（要望：Lv1～Lv10 の上に、採掘・園芸それぞれ 2 つ＝合計 4 か所）。
        ImGui.TableNextRow();
        for (var col = 0; col < 2; col++)
        {
            ImGui.TableSetColumnIndex(col);
            DrawBulk((GatherableCatalog.Job)col, block, panel, requireData);
        }

        for (var i = 0; i < 10; i++)
        {
            ImGui.TableNextRow();
            for (var col = 0; col < 2; col++)
            {
                ImGui.TableSetColumnIndex(col);
                ImGui.Dummy(new Vector2(0, ImGui.GetTextLineHeight() * 0.5f));
                var job = (GatherableCatalog.Job)col;
                var band = new GatherableCatalog.LevelBand(i);
                var selected = profiles.Selected(GatherProfileKind.Stock, job, band);
                var rows = Rows(panel, job, band);
                using (ImRaii.Disabled(block is not null || !Selectable(panel, requireData, job, band)))
                {
                    if (ImGui.Checkbox($"Lv{band.MinLevel}～Lv{band.MaxLevel}##stock{col}-{i}", ref selected))
                    {
                        // 右の欄に打ち込んでいる途中の数を、チェックを入れる前に保存する（チェックを入れた時点の数で登録するため）。
                        if (selected) profileTabs.Quantities.CommitPending(profiles, job, band);
                        profiles.Set(GatherProfileKind.Stock, job, band, selected);
                    }
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.BeginTooltip();
                    try { DrawBandTooltip(job, band, selected, rows, block ?? (HasData(panel) ? null : noData ?? panel?.Problem), retainerOf); }
                    finally { ImGui.EndTooltip(); }
                }
                // チェックボックスの右に、この帯の目標の数（チェックが入っている間は変えられない）。
                profileTabs.Quantities.Draw(profiles, job, band, selected, block is not null, $"stock{col}-{i}");
            }
        }
    }

    /// <summary>下のボタン：「Auto-Gatherに追加」「所持数・採取情報を再確認」と、その下に「Auto-Gatherに停止」。</summary>
    private static void DrawButtons(string? applyBlock, Action apply, string? recheckBlock, Action recheck, string? stopBlock, Action stop)
    {
        using (ImRaii.Disabled(applyBlock is not null))
        {
            if (ImGui.Button("Auto-Gatherに追加##stockApply"))
                apply();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UnvisitedFeature.ButtonTooltip(applyBlock, ApplyTooltip);

        // 再確認は画面の数を読み直すだけ（GBR へは書かない。書くのは「Auto-Gatherに追加」だけにした）。
        ImGui.SameLine();
        using (ImRaii.Disabled(recheckBlock is not null))
        {
            if (ImGui.Button("所持数・採取情報を再確認"))
                recheck();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UnvisitedFeature.ButtonTooltip(recheckBlock, "鞄の所持数と採取手帳の記録を読み直して、この画面の件数を新しくします。\nGBR のリストは変えません。");

        using (ImRaii.Disabled(stopBlock is not null))
        {
            if (ImGui.Button("Auto-Gatherに停止##stockStop"))
                stop();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UnvisitedFeature.ButtonTooltip(stopBlock, UnvisitedFeature.StopTooltip);
    }

    /// <summary>
    /// Lv 帯のチェックの吹き出し（指定の形）。
    /// 希望所持数なし：「チェックを入れると、100個ずつ採取してきます。」＋「ファイアシャード：所持1001 → 1101個まで」。
    /// 希望所持数あり：「チェックを入れると、鞄とリテイナーの合計が100個になるまで採取してきます。」＋
    ///   「品：所持（鞄＋リテイナー）の合計 → 100個まで（あと○個）」、もう足りていれば「足りているので採りません」。
    ///   リテイナーの数は Allagan Tools から読む（乗せている品だけ 10 秒に 1 回。ほかのキャラクターは、そのゲームで読んでもらう）。
    ///   GBR のリストの数は「鞄がこの数になるまで」なので、リストには「100 − リテイナーの数」を入れる（＝差分の 100 −（鞄＋リテイナー）だけ採る。MaterialPlan.Stock）。
    /// チェック済みの帯は「登録している品」と、同じ形の行。
    /// </summary>
    private void DrawBandTooltip(GatherableCatalog.Job job, GatherableCatalog.LevelBand band, bool selected,
        List<MaterialPlan.Row> rows, string? block, Func<uint, (bool Known, int? Count, string Error)> retainerOf)
    {
        if (block is not null) { ImGui.TextUnformatted(block); return; }

        var n = profiles.Quantity(job, band);
        var desired = profiles.DesiredTotal(job, band);
        // 希望所持数でない帯は、リテイナーの数を読まない（吹き出しにも出さない）。
        (bool Known, int? Count, string Error) notRead = (true, 0, "");
        var lines = rows.Select(r => (Row: r, Retainer: desired ? retainerOf(r.ItemId) : notRead)).ToList();
        // 希望所持数の帯の件数は、足りていない品（GBR のリストに入る品）だけ数える（2026-10-07。前は「足りているので採りません」の品も数えていた）。
        // リテイナーの数をまだ読めていない品があるうちは、候補の件数のまま。
        var registered = desired && lines.All(l => l.Retainer.Known && l.Retainer.Count is not null)
            ? lines.Count(l => (long)l.Row.Held + l.Retainer.Count!.Value < n)
            : rows.Count;
        ImGui.TextUnformatted(selected ? $"登録している品：{registered}件"
            : desired ? $"チェックを入れると、鞄とリテイナーの合計が{n}個になるまで採取してきます。"
            : $"チェックを入れると、{n}個ずつ採取してきます。");

        foreach (var (row, (known, retainer, error)) in lines)
        {
            if (!desired)
            {
                ImGui.TextUnformatted($"{row.Name}：所持{row.Held} → {row.Target}個まで");
                continue;
            }

            if (!known)
            {
                ImGui.TextUnformatted($"{row.Name}：鞄{row.Held}・リテイナー 読み込み中");
                continue;
            }

            if (retainer is not { } r)
            {
                ImGui.TextUnformatted($"{row.Name}：鞄{row.Held}・リテイナー 不明（{error}）");
                continue;
            }

            var total = (long)row.Held + r;
            ImGui.TextUnformatted(total >= n
                ? $"{row.Name}：所持{total}（鞄{row.Held}＋リテイナー{r}） → 足りているので採りません"
                : $"{row.Name}：所持{total}（鞄{row.Held}＋リテイナー{r}） → {n}個まで（あと{n - total}個）");
        }
    }

    /// <summary>全素材の補充の「Auto-Gatherに追加」の説明（解放採取のボタンとは別物。この機能のリストだけを変える）。</summary>
    internal const string ApplyTooltip =
        "チェックした Lv 帯の素材を、GBR の Auto-Gather に追加します（外した帯のリストは消します）。";
}
