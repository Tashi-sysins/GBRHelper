using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using GBRHelper.Ipc;
using GBRHelper.Ui;

namespace GBRHelper.Features;

/// <summary>
/// Crafting Listsから末端素材抽出。
/// Artisan の Crafting Lists をプルダウンで選び、「Auto-Gatherに追加する」を押すと、そのリストを作るための素材のうち
/// GBR で採れる品を、GBR の Auto-Gather のプリセットにする。
///
/// 【中身は GBR の「Artisan から読み込む」と同じ】利用者が GBR の Auto-Gather の画面のボタンで使えることを確かめた仕組み
///   （GatherBuddy/AutoGather/Helpers/Reflection.cs の ArtisanExporter）を、GBRHelper の画面からも使えるようにしたもの。
///   素材は Artisan の ListMaterials（ArtisanListAccess）、GBR に入れる品の決まりは GbrAutoGatherListAccess.ResolveForAutoGather。
///   数は Artisan が出した必要数（GBR の数は「その数になるまで採る」）。手持ちの完成品・中間素材は差し引かない（GBR の取り込みと同じ）。
/// 【GBR の取り込みと違うところ】
///   ・リストは GBRHelper の印つき（名前 GBRHelper_製作_リスト名）。同じ Crafting List で押し直すと置き換える（GBR のボタンは押すたびに増える）。
///   ・有効の状態で作る（ほかの「Auto-Gatherに追加」と同じ。GBR の取り込みは無効で作る）。
///   ・書き込みはゲームの更新の流れで行い、保存ファイルで確かめる（GBR のボタンは別のスレッドで書く）。
///   ・GBR の自動採集中は押せない（橙色「採取中につき操作を受け付けられません」。ほかの「Auto-Gatherに追加」と同じ）。
/// </summary>
public sealed class ArtisanFeature(ArtisanListAccess artisan, GbrAutoGatherListAccess lists, GatherBuddyIpc gbr, Func<string?> busyReason, Configuration config,
    ItemFolklore folklore) : IFeature
{
    public const string FeatureName = "Crafting Listsから末端素材抽出";

    /// <summary>プルダウンの何も選んでいないときの表示（指定の文）。</summary>
    public const string Placeholder = "--Crafting Listsを選択して下さい--";

    /// <summary>ボタンの下の案内（指定の文）。</summary>
    public const string GbrImportNote = "※Auto-Gatherタブ内の「Artisanから読み込む」からでも取り込めます";

    /// <summary>GBR のリストの名前の頭（ほかの GBRHelper のリストと同じく GBRHelper_ で始める）。</summary>
    public const string ListNamePrefix = "GBRHelper_製作_";

    /// <summary>GBR のリストの説明欄の印（どの Crafting List から作ったか。ほかの機能のリストと混ぜない）。</summary>
    public static string Tag(int listId) => $"[GBRHelper:ArtisanMaterials][List:{listId}]";

    public string Name => FeatureName;
    public string Description => "Artisan の Crafting Lists を選んで、作るのに要る素材（GBR で採れる品）を GBR の Auto-Gather に追加します。";
    public int SortOrder => 125;
    public bool Enabled { get => true; set { } }
    public bool HideEnableToggle => true;

    private Dictionary<int, string>? names;
    private string namesError = "";
    private DateTime nextNames;
    private int? selected;
    private int? requested;
    private string result = "";
    private List<string> skippedNames = new();

    /// <summary>伝承録を読んでいないので入れなかった素材の名前（2026-10-06）。</summary>
    private List<string> folkloreNames = new();

    /// <summary>伝承録を読んだか確かめられないので入れなかった素材の名前。</summary>
    private List<string> folkloreUnknownNames = new();

    public void ResetCharacter()
    {
        selected = requested = null; names = null; nextNames = default;
        result = namesError = ""; ClearSkipped();
    }

    private void ClearSkipped()
    {
        this.skippedNames.Clear();
        this.folkloreNames.Clear();
        this.folkloreUnknownNames.Clear();
    }

    public void Tick()
    {
        // 一覧は 2 秒ごとに読み直す（Artisan でリストを作った・消したのが出るように）。毎フレームは読まない。
        if (DateTime.UtcNow >= this.nextNames)
        {
            this.nextNames = DateTime.UtcNow.AddSeconds(2);
            this.names = artisan.ListNames();
            this.namesError = this.names is null ? artisan.LastError : "";
            // 選んでいたリストが消えたら、選んでいない状態に戻す。
            if (this.selected is { } id && this.names is not null && !this.names.ContainsKey(id))
                this.selected = null;
        }

        if (this.requested is { } listId)
        {
            this.requested = null;
            this.Run(listId);
        }
    }

    /// <summary>押せない理由（押せるなら null）。</summary>
    private string? Block()
    {
        if (Svc.PlayerState.ContentId == 0) return "ログインしていません";
        if (this.names is null) return this.namesError.Length != 0 ? this.namesError : "Artisan の Crafting Lists を読み込んでいます";
        if (this.selected is null) return "Crafting Lists を選んでください";
        return gbr.IsAutoGatherEnabled() switch
        {
            null => "GatherBuddyReborn の状態を読めません",
            true => GatherProfileController.AutoGatheringText,
            false => busyReason(),
        };
    }

    public void DrawRight()
    {
        // プルダウン（指定：一番上の表示は「--Crafting Listsを選択して下さい--」）。
        var preview = this.selected is { } id && this.names?.GetValueOrDefault(id) is { } current ? current : Placeholder;
        ImGui.SetNextItemWidth(Math.Max(ImGui.CalcTextSize(Placeholder).X + ImGui.GetFrameHeight() * 2, ImGui.GetContentRegionAvail().X * 0.6f));
        using (var combo = ImRaii.Combo("##craftingLists", preview))
        {
            if (combo)
            {
                var items = this.names?.OrderBy(p => p.Value, StringComparer.CurrentCulture).ThenBy(p => p.Key).ToArray() ?? [];
                if (items.Length == 0)
                {
                    // リストが無いとき：何もない空欄の行だけ（選べない）。
                    using (ImRaii.Disabled())
                        ImGui.Selectable("##noCraftingLists", false, ImGuiSelectableFlags.Disabled);
                }

                foreach (var (listId, name) in items)
                {
                    // 同じ名前のリストがあっても区別できるように、見えない部分に ID を入れる。
                    var same = items.Count(p => p.Value == name) > 1;
                    if (ImGui.Selectable($"{name}{(same ? $"（{listId}）" : "")}##artisan{listId}", this.selected == listId))
                    {
                        if (this.selected != listId) { this.result = ""; this.ClearSkipped(); }
                        this.selected = listId;
                    }
                }
            }
        }

        if (this.names is null && this.namesError.Length != 0)
            ImGui.TextColored(ImGuiColors.DalamudYellow, this.namesError);

        // プルダウンの下に「Auto-Gatherに追加する」（指定の文）。
        var block = Block();
        using (ImRaii.Disabled(block is not null || this.requested is not null))
        {
            if (ImGui.Button("Auto-Gatherに追加する##artisanApply"))
                this.requested = this.selected;
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UnvisitedFeature.ButtonTooltip(block,
                "選んだ Crafting List を作るのに要る素材のうち、GBR で採れる品を GBR の Auto-Gather に追加します。\n"
                + "伝承録を読んでいないと採れない品は入れません（GBR が採集点の前で待ち続けるため）。\n"
                + $"リストの名前は「{ListNamePrefix}（Crafting List の名前）」。同じ Crafting List で押し直すと、そのリストを作り直します。");

        // GBR 自身の取り込みの場所の案内（指定の文）。
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            ImGui.TextWrapped(GbrImportNote);

        if (this.result.Length != 0)
            ImGui.TextWrapped(this.result);

        if (this.skippedNames.Count > 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, $"GBR で採れない素材 {this.skippedNames.Count} 品は入れていません（購入品・ドロップ品・製作品など）");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Join("\n", this.skippedNames));
        }

        // 伝承録を読んでいない品（確認：読んでいないと採れないので、プリセットから外す）。
        if (this.folkloreNames.Count > 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"伝承録を読んでいない素材 {this.folkloreNames.Count} 品は入れていません（伝承録を読んでから押し直すと入ります）");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Join("\n", this.folkloreNames));
        }

        if (this.folkloreUnknownNames.Count > 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, $"伝承録を読んだか確かめられない素材 {this.folkloreUnknownNames.Count} 品は入れていません");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Join("\n", this.folkloreUnknownNames));
        }
    }

    /// <summary>そのリストの素材を GBR に書く（ゲームの更新の流れで。押した直前の状態をもう一度確かめる）。</summary>
    private void Run(int listId)
    {
        this.ClearSkipped();
        if (Block() is { } block) { this.result = "追加しませんでした：" + block; return; }
        if (this.names?.GetValueOrDefault(listId) is not { } name) { this.result = "追加しませんでした：Crafting List が見つかりません"; return; }

        if (artisan.Materials(listId) is not { } materials) { this.result = "追加しませんでした：" + artisan.LastError; return; }
        if (lists.ResolveForAutoGather(materials) is not { } resolved) { this.result = "追加しませんでした：" + lists.LastError; return; }

        this.skippedNames = resolved.Skipped.Select(ItemName).ToList();
        if (resolved.Entries.Count == 0)
        {
            this.result = $"「{name}」の素材に、GBR で採れる品がありません。";
            return;
        }

        // 伝承録を読んでいないと採れない品を外す（2026-10-06。GBR は伝承録を見ずに採集点の前で待ち続けるため）。
        if (!folklore.Prepare()) { this.result = "追加しませんでした：" + folklore.LastError; return; }
        var (entries, notRead, unknown) = ItemFolklore.Filter(resolved.Entries, folklore.Ok);
        this.folkloreNames = notRead.Select(ItemName).ToList();
        this.folkloreUnknownNames = unknown.Select(ItemName).ToList();
        if (entries.Count == 0)
        {
            this.result = $"「{name}」の素材は、伝承録を読んでいない（または確かめられない）品だけでした。追加しませんでした。";
            return;
        }

        var listName = ListNamePrefix + name;
        var write = WriteImport(config, listId, listName, previous => ArtisanManagedWriter.Write(lists, listName, Tag(listId), entries, previous));
        this.result = write.Ok
            ? $"GBR の Auto-Gather に「{listName}」を追加しました（{entries.Count} 品）。"
            : "追加しませんでした：" + write.Error;
        Svc.Log.Information($"[GBRHelper] Crafting Lists から末端素材抽出：{this.result}");
    }

    /// <summary>GBRへの書き込み前に再試行用の名前を確定する。保存失敗を成功表示にしない。</summary>
    public static GbrAutoGatherListAccess.WriteResult WriteImport(Configuration config, int id, string name,
        Func<string, GbrAutoGatherListAccess.WriteResult> write)
    {
        var previous = config.ArtisanListNames.GetValueOrDefault(id, name);
        if (config.ArtisanPendingNames.TryGetValue(id, out var pending) && pending != name)
            return GbrAutoGatherListAccess.WriteResult.Fail($"前回の追加が完了していません。Artisan の名前を「{pending[ListNamePrefix.Length..]}」に戻して追加を再試行してください。");
        try
        {
            var hadPending = config.ArtisanPendingNames.TryGetValue(id, out var oldPending);
            config.ArtisanPendingNames[id] = name;
            try { config.Save(); } // これが失敗したらGBRには触れない。
            catch
            {
                if (hadPending) config.ArtisanPendingNames[id] = oldPending!;
                else config.ArtisanPendingNames.Remove(id);
                throw;
            }
            var result = write(previous);
            if (!result.Ok) return result;
            config.ArtisanListNames[id] = name;
            config.ArtisanPendingNames.Remove(id);
            try { config.Save(); }
            catch
            {
                config.ArtisanListNames[id] = previous;
                config.ArtisanPendingNames[id] = name;
                throw;
            }
            return result;
        }
        catch (Exception ex) { return GbrAutoGatherListAccess.WriteResult.Fail("記録を保存できません。再試行してください：" + ex.GetBaseException().Message); }
    }

    private static string ItemName(uint itemId)
    {
        try
        {
            return Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(itemId, out var row) && row.Name.ExtractText() is { Length: > 0 } n
                ? n
                : $"品番 {itemId}";
        }
        catch
        {
            return $"品番 {itemId}";
        }
    }
}
