using System.Collections.Generic;

namespace GBRHelper.Features;

/// <summary>
/// 霊砂・クリスタルを同時に登録できるようにしたとき（要望「霊砂のプリセットを設定した時、クリスタルの
/// プリセットが設定出来ないのは辞めて。どっちも設定出来るように」）の、登録の止め方とリストの並びの決まり（ゲームに触れない判断だけ）。
///
/// 【共有するもの】GBR の設定の書き換え（並べ方なし・精選・全部精選）と、戻すための記録（Configuration.TimedRecoveryTag ほか）は
/// 2 つの登録で 1 つ。最初の登録で書き換え、最後の登録を止めたときに戻す。2 つのリストは同じ管理の印を持つ。
/// </summary>
public static class TimedRunRules
{
    /// <summary>登録を 1 つ止めるときに、いま行うこと。</summary>
    public enum StopStep
    {
        /// <summary>採集・精選の切れ目まで待つ（GBR が動いている間に、リストや設定を途中で変えない）。</summary>
        Wait,

        /// <summary>このリストだけ消す。ほかの登録が続くので、GBR は止めず、GBR の設定も戻さない。</summary>
        RemoveListOnly,

        /// <summary>最後の登録：GBR の自動採集を止める（止まったら RecoverAll）。</summary>
        StopGbr,

        /// <summary>最後の登録：管理リストを全部消し、GBR の設定を戻す（GBR は止まっている）。</summary>
        RecoverAll,
    }

    /// <param name="othersActive">止める登録のほかに、続ける登録があるか。</param>
    /// <param name="gbrOn">GBR の自動採集が ON か。</param>
    /// <param name="safeBoundary">採集・精選の切れ目か（GBR が ON のときだけ見る。止まっていれば待たない）。</param>
    public static StopStep Stop(bool othersActive, bool gbrOn, bool safeBoundary)
        => gbrOn && !safeBoundary ? StopStep.Wait
            : othersActive ? StopStep.RemoveListOnly
            : gbrOn ? StopStep.StopGbr
            : StopStep.RecoverAll;

    /// <summary>
    /// Auto-Gather での管理リストの並び（上から）。霊砂のリストを一番上、クリスタルのリストをその下に置く。
    /// 【霊砂を上にする理由】並べ方「なし」の GBR は、同時に出ている刻限の品をリストの上から採る。霊砂はその原料からしか作れないが、
    /// 霊砂の原料は精選でクリスタル・クラスターもほぼ出す（刻限の採集点で霊砂が出る 79 行のうち 76 行。2026-10-06 ゲームデータで確認。
    /// 霊砂だけ出るのはスペアミント・アルマンディン）。霊砂を先に採ってもクリスタルは増えるが、逆だと霊砂は増えない。
    /// </summary>
    public static IReadOnlyList<string> ListOrder(bool sand, bool crystal, string sandList, string crystalList)
    {
        var names = new List<string>();
        if (sand) names.Add(sandList);
        if (crystal) names.Add(crystalList);
        return names;
    }
}
