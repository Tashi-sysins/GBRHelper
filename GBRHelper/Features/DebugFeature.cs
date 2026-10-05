using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using GBRHelper.Translation;
using GBRHelper.Ui;

namespace GBRHelper.Features;

/// <summary>
/// デバッグ。左上の「機能」の文字を 5 回続けて押すと左ペインに出る（もう一度 5 回で隠れる。保存しない）。
/// GBR の日本語表示の状態（フック・辞書・訳している文言の種類・タブを戻した回数・例外）と、
/// 「訳の無かった英語を集める」（辞書を足すための記録）をここに置く（日本語表示の画面からは外した）。
/// </summary>
public sealed class DebugFeature(TranslationFeature translation) : IFeature
{
    public const string FeatureName = "デバッグ";

    public string Name => FeatureName;

    public string Description => "開発用の表示です。";

    public int SortOrder => 900;

    public bool Enabled
    {
        get => true;
        set { }
    }

    public bool HideEnableToggle => true;

    public void DrawRight()
    {
        ImGui.TextColored(ImGuiColors.DalamudGrey, "GBR の日本語表示");
        if (translation.Hooks is { } h)
        {
            ImGui.TextUnformatted($"フック {h.Installed}/{h.Requested}・訳している文言 {h.TranslatedKinds:N0} 種類・タブの並びを戻した回数 {h.TabOrderFixes:N0}");
            if (h.Installed < h.Requested)
                foreach (var m in h.Missing)
                    ImGui.TextColored(ImGuiColors.DalamudYellow, "・置けなかったフック：" + m);
            if (h.ErrorCount > 0)
                ImGui.TextColored(ImGuiColors.DalamudYellow, $"差し替えの途中の例外 {h.ErrorCount:N0} 回（最初：{h.FirstError}）");
        }
        else
        {
            ImGui.TextUnformatted(translation.Error.Length != 0 ? "開始できませんでした：" + translation.Error : "フックは置いていません（無効・または準備中）");
        }

        if (translation.Table is { } t)
            ImGui.TextUnformatted($"辞書：{t.ExactCount:N0} 件（決まった文）＋ {t.TemplateCount:N0} 件（数値などが入る文）");

        ImGui.Separator();

        // ---- 訳の無かった英語を集める（辞書を足すための記録。日本語表示の画面から移した） ----
        var collect = translation.Collect;
        if (ImGui.Checkbox("訳の無かった英語を集める", ref collect))
            translation.Collect = collect;

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"GBR の画面に出たのに辞書に無かった英語を記録します（最大 {GbrTextHooks.UntranslatedLimit} 件）。どこにも送りません。");

        if (translation.Hooks is { } hc)
        {
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudGrey, $"{hc.Untranslated.Count:N0} 件");

            if (ImGui.Button("集めた英語をクリップボードへコピー"))
                ImGui.SetClipboardText(string.Join("\n", hc.Untranslated.OrderBy(s => s, StringComparer.Ordinal)
                    .Select(s => s.Replace("\\", "\\\\").Replace("\n", "\\n"))));

            ImGui.SameLine();
            if (ImGui.Button("集めた英語を消す"))
                hc.ClearUntranslated();
        }
    }
}
