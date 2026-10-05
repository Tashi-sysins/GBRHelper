using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using GBRHelper.Features;

namespace GBRHelper.Ui;

/// <summary>
/// 全素材の補充の、Lv 帯のチェックボックスの右に置く「さらに採る数」の入力欄。
///
/// 【確定のしかた】打ち込んでいる途中の数は pending に持ち、入力欄から離れたとき（IsItemDeactivatedAfterEdit）に保存する。
///   1 文字ごとに保存すると、範囲に収める処理で打ち込みの途中の数（例：「0」）が書き換わってしまうため。
/// 【チェックとの順番】ImGui は描いた順に操作を処理する。左のチェックボックスが先に ON になると、右の入力欄の確定は
///   「チェックが入っている帯は変えない」で捨てられる。そのため、チェックを入れる前に CommitPending を呼んで、途中の数を先に保存する。
/// </summary>
public sealed class StockQuantityEditor
{
    private readonly Dictionary<(ulong Character, int Key), int> pending = new();

    /// <summary>
    /// 数の欄と「希望所持数」のチェックを置く位置（表のセルの左端から）。どの Lv 帯でも縦にそろい、上の「希望所持数（一括）」も
    /// 同じ位置に置けるよう、一番長い「Lv91～Lv100」のチェックに合わせる（要望：一括のチェックを真上に）。
    /// </summary>
    public static (float Input, float Desired) Columns()
    {
        var style = ImGui.GetStyle();
        var scale = ImGuiHelpers.GlobalScale;
        var input = ImGui.GetFrameHeight() + style.ItemInnerSpacing.X + ImGui.CalcTextSize("Lv91～Lv100").X + style.ItemSpacing.X;
        var desired = input + InputWidth * scale + style.ItemSpacing.X + ImGui.CalcTextSize("個").X + style.ItemSpacing.X;
        return (input, desired);
    }

    private const float InputWidth = 64;

    /// <summary>
    /// 直前の部品の右に入力欄と「個」と「希望所持数」を描く。selected（帯にチェックが入っている）か blocked（ログインしていないなど）なら変えられない。
    /// 数の欄の吹き出し（「チェックが入っている間は変更出来ません。…」）は、帯にチェックが入っているときだけ出す。
    /// </summary>
    public void Draw(GatherProfileController profiles, GatherableCatalog.Job job, GatherableCatalog.LevelBand band, bool selected, bool blocked, string id)
    {
        var locked = selected || blocked;
        var key = (profiles.EditingCharacter, GatherProfiles.Key(job, band));
        var value = this.pending.TryGetValue(key, out var p) ? p : profiles.Quantity(job, band);
        var (inputX, desiredX) = Columns();

        ImGui.SameLine(inputX);
        ImGui.SetNextItemWidth(InputWidth * ImGuiHelpers.GlobalScale);
        using (ImRaii.Disabled(locked))
        {
            if (ImGui.InputInt($"##qty{id}", ref value))
                this.pending[key] = value;

            if (ImGui.IsItemDeactivatedAfterEdit())
                this.CommitPending(profiles, job, band);
        }

        // 説明は指定の一文だけにした。チェックが入っていない帯では出さない（変えられるので）。
        if (selected && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("チェックが入っている間は変更出来ません。変更したい時は、一旦チェックを外してから個数変更を行ってください");

        ImGui.SameLine();
        ImGui.TextUnformatted("個");

        // 「希望所持数」：チェック無し＝チェックした時の手持ちからさらに N 個、
        // チェック有り＝鞄＋リテイナーの合計が N 個になるまで。数の欄と同じく、帯にチェックが入っている間は変えない。
        ImGui.SameLine(desiredX);
        var desired = profiles.DesiredTotal(job, band);
        using (ImRaii.Disabled(locked))
        {
            if (ImGui.Checkbox($"希望所持数##desired{id}", ref desired))
                profiles.SetDesiredTotal(job, band, desired);
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("チェック無し：チェックを入れた時の手持ちから、さらに左の数だけ採ります。\n" +
                             "チェック有り：鞄とリテイナーの合計が左の数になるまで採ります（リテイナーの数は Allagan Tools で数えます）。");
    }

    /// <summary>打ち込んでいる途中の数があれば保存する（チェックを入れる直前に呼ぶ）。</summary>
    public void CommitPending(GatherProfileController profiles, GatherableCatalog.Job job, GatherableCatalog.LevelBand band)
    {
        var key = (profiles.EditingCharacter, GatherProfiles.Key(job, band));
        if (this.pending.Remove(key, out var value))
            profiles.SetQuantity(job, band, value);
    }
}
