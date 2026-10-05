using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using GBRHelper.Features;

namespace GBRHelper.Ui;

/// <summary>
/// 解放採取・補充は現在ログイン中のキャラクターのタブだけ表示する。
/// 他のキャラクターの情報は保存したまま、そのキャラクターでログインしたときだけ使う。
/// </summary>
public sealed class GatherProfileTabs
{
    public static bool IsVisible(ulong candidate, ulong current) => current != 0 && candidate == current;

    private ulong lastCharacter;

    /// <summary>全素材の補充の「目標の数」の入力欄。</summary>
    public StockQuantityEditor Quantities { get; } = new();

    public void Draw(GatherProfileController profiles, GatherProfileKind kind, Action<ulong> drawTab)
    {
        using var tabs = ImRaii.TabBar($"gatherProfiles{kind}");
        if (!tabs) return;
        var selectCurrent = profiles.Character != 0 && lastCharacter != profiles.Character;
        if (profiles.Character == 0)
            ImGui.TextWrapped("キャラクターでログインすると、名前のタブが自動的に追加されます。");
        foreach (var pair in profiles.Profiles.Where(p => IsVisible(p.Key, profiles.Character)).ToArray())
        {
            var name = pair.Value.Name.Length == 0 ? $"キャラクター {pair.Key}" : pair.Value.Name;
            Tab(pair.Key, name);
        }
        if (profiles.Profiles.ContainsKey(profiles.Character)) lastCharacter = profiles.Character;
        void Tab(ulong cid, string label)
        {
            using var tab = ImRaii.TabItem($"{label}##profile{cid}", selectCurrent && cid == profiles.Character ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None);
            if (!tab) return;
            profiles.Edit(cid);
            using var scope = ImRaii.PushId($"profile{cid}");
            // 冒頭の説明文（キャラクター専用・OFF で削除 など）は要望で外した（「全部要らない」）。
            drawTab(cid);
        }
    }
}
