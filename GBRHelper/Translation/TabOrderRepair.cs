using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Bindings.ImGui;

namespace GBRHelper.Translation;

/// <summary>
/// GBR のメイン画面のタブの並びを、GBR がタブを出す順（GBR のソースの順）に保つ。
///
/// 【なぜ要るか】2026-10-05 以前の版は訳すとタブの ID が変わり、GBR の「並べ替えできるタブバー」では ID の変わったタブが末尾へ回った。
///   いまは ID を元のままにして訳す（ImGuiLabel）が、ImGui はタブの並びをゲームを閉じるまで覚えている。
///   新しい DLL に替えても、同じゲームの中では崩れた並びが残る（imgui 1.88：知らない ID のタブは末尾に足し、並べ替えできる
///   タブバーは出した順に並べ直さない。imgui_widgets.cpp 7450・8194）。
/// 【どう戻すか】タブを出した順は各タブの BeginOrder に残っている（そのフレームで何番目に出したか。imgui_widgets.cpp 8209）。
///   前のフレームにタブバーが出ていたとき、そのフレームの最初のタブより前（ImGui がタブを並べる前）に、BeginOrder の順に並べ直す。
/// 【毎フレーム確かめる】（2026-10-05 12時に「一度だけ」から変更）
///   11:07 の版（一度だけ戻す）で、利用者の画面では自動採集のタブだけが末尾にあった。同じ流れを試験で再現しても元に戻るので、
///   ゲームの中だけで起きる何かでずれた。要望「初期位置の場所から動かさないで」に合わせ、原因によらず毎フレーム確かめて戻す。
///   ドラッグで並べ替えても次のフレームで戻る。並びが正しいときは何も書き換えない（読むだけ）。
/// </summary>
public static unsafe class TabOrderRepair
{
    /// <summary>
    /// 並びがずれていたら、出した順に並べ直す。並べ直したら true。
    /// 並びが正しい・条件がそろっていない（最初のタブより後・前のフレームに出ていない）ときは何もせず false。
    /// </summary>
    /// <param name="bar">いまのタブバー（ImGuiContext.CurrentTabBar）。</param>
    /// <param name="frame">いまのフレーム番号（ImGui.GetFrameCount()）。</param>
    public static bool Keep(ImGuiTabBar* bar, int frame)
    {
        if (!NeedsKeep(bar, frame))
            return false;

        var n = bar->Tabs.Size;
        var items = new ImGuiTabItem[n];
        for (var i = 0; i < n; i++)
            items[i] = bar->Tabs.Data[i];

        // 前のフレームに出なかったタブ（古い ID の残り）は、このフレームの最初のタブで ImGui が消す（imgui_widgets.cpp 7546）。
        // 残るタブ同士の順は BeginOrder だけで決まるので、古いタブの置き場所は気にしなくてよい。
        var sorted = items
            .Select((t, i) => (Tab: t, Index: i))
            .OrderBy(x => x.Tab.BeginOrder)
            .ThenBy(x => x.Index)
            .Select(x => x.Tab)
            .ToArray();

        for (var i = 0; i < n; i++)
            bar->Tabs.Data[i] = sorted[i];

        return true;
    }

    /// <summary>いま並べ直すべきか（並べ直せる時で、かつ並びがずれている）。読むだけで何も書き換えない。</summary>
    public static bool NeedsKeep(ImGuiTabBar* bar, int frame)
    {
        if (bar == null)
            return false;

        // そのフレームの最初のタブより前でないと、BeginOrder がこのフレームの物と前のフレームの物で混ざる。
        if (bar->TabsActiveCount != 0)
            return false;

        // 前のフレームにタブバーが出ていないと、BeginOrder が古い（画面を開き直した直後など）。
        if (bar->PrevFrameVisible != frame - 1)
            return false;

        return bar->Tabs.Size > 1 && !InOrder(bar);
    }

    /// <summary>前のフレームに出たタブ（このフレームで残るタブ）が、出した順に並んでいるか。</summary>
    private static bool InOrder(ImGuiTabBar* bar)
    {
        var prev = bar->PrevFrameVisible;
        var last = short.MinValue;
        for (var i = 0; i < bar->Tabs.Size; i++)
        {
            var t = &bar->Tabs.Data[i];
            if (t->LastFrameVisible < prev)
                continue;
            if (t->BeginOrder < last)
                return false;
            last = t->BeginOrder;
        }

        return true;
    }

    /// <summary>
    /// いまの並びのタブの名前（記録用）。前のフレームに出たタブだけ、並びの順に。名前は「##」より前の見える部分。
    /// </summary>
    public static string Describe(ImGuiTabBar* bar)
    {
        if (bar == null)
            return string.Empty;

        var names = new List<string>();
        var buf = bar->TabsNames.Buf;
        for (var i = 0; i < bar->Tabs.Size; i++)
        {
            var t = &bar->Tabs.Data[i];
            if (t->LastFrameVisible < bar->PrevFrameVisible)
                continue;
            if (t->NameOffset < 0 || t->NameOffset >= buf.Size)
            {
                names.Add("?");
                continue;
            }

            var p = buf.Data + t->NameOffset;
            var len = 0;
            while (t->NameOffset + len < buf.Size && p[len] != 0 && !(p[len] == (byte)'#' && t->NameOffset + len + 1 < buf.Size && p[len + 1] == (byte)'#'))
                len++;
            names.Add(Encoding.UTF8.GetString(p, len));
        }

        return string.Join("・", names);
    }
}
