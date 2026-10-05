using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace GBRHelper.Ui;

/// <summary>
/// 押すと中身を開いたり閉じたりする見出し。AutoDuty の設定タブ（AutoDuty/Windows/Config.cs の各 Header）と同じ形にしている：
/// 区切り線の下に、文字を中央に寄せた選択行を置き、押すたびに開閉を切り替える。指を乗せると手の形のカーソルになる。
/// </summary>
public static class FoldingHeader
{
    /// <summary>見出しを描く。開いていれば true（中身を続けて描く）。</summary>
    /// <param name="label">見出しの文字。同じ画面に同じ文字の見出しを並べるときは「##」で区別する。</param>
    /// <param name="open">開いているか。押されたら反転する。</param>
    /// <param name="color">
    /// 見出しの帯の色（要望「霊砂のバーとクリスタル・クラスターのバーに、見えやすい違う色を付ける」）。
    /// 指定すると、閉じていても帯に色を付ける（開いているときは濃く、閉じているときは薄く、指を乗せると一番濃く）。null なら色を付けない（選んだ行と同じ）。
    /// </param>
    public static bool Draw(string label, ref bool open, Vector4? color = null)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0.5f, 0.5f));
        var colors = 0;
        if (color is { } c)
        {
            ImGui.PushStyleColor(ImGuiCol.Header, c with { W = open ? 0.85f : 0.55f });
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, c with { W = 1f });
            ImGui.PushStyleColor(ImGuiCol.HeaderActive, c with { W = 1f });
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, 1f));
            colors = 4;
        }

        // 色を付けるときは、閉じていても帯が見えるように「選んだ行」として描く（押したかどうかは戻り値で見る）。
        var clicked = ImGui.Selectable(label, color is not null || open, ImGuiSelectableFlags.DontClosePopups);
        if (colors > 0)
            ImGui.PopStyleColor(colors);
        ImGui.PopStyleVar();
        if (ImGui.IsItemHovered())
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (clicked)
            open = !open;
        return open;
    }
}
