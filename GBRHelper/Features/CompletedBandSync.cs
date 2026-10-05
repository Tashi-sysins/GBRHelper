using System;
using System.Collections.Generic;

namespace GBRHelper.Features;

/// <summary>
/// 「ベンチャー未採取品の採取」の 5 秒ごとの確認で、Lv 帯のリストを作り直すかを決める（ゲームに触れない判断だけ）。
/// ・リストがある帯の対象品がなくなった → 作り直してリストを消す（要望「対象品がなくなったら自動的にプリセット削除」）。
///   チェックは残すので、画面ではチェック済みのまま暗くなる。
/// ・空なのでリストが無い帯に対象品が戻った（伝承録を読んだなど） → 作り直してリストを戻す
///   （確認「伝承録を読んで採取出来る様に成ったら、再びチェックできる様になる」。チェックは残っているので、そのまま戻す）。
/// 同じ帯には 1 回だけ頼む（手で足した品が残っていると作り直してもリストは消えず、頼み続けると記録が増え続ける）。
/// 品が残っているのにリストが無い帯（GBR で手で消したなど）は、空になったのを見ていないので戻さない。
/// </summary>
public sealed class CompletedBandSync
{
    public enum Action { None, Remove, Restore }

    /// <summary>帯 1 つの今の様子。Name はリスト名、HasList は自分のリストが GBR にあるか。件数は UnvisitedPlan.Summarize の数。</summary>
    public readonly record struct Band(string Name, bool HasList, int Ungathered, int Unknown);

    /// <summary>リストの削除を頼んだ帯（リスト名）。対象品が戻ったら外す。</summary>
    private readonly HashSet<string> removeRequested = new(StringComparer.Ordinal);

    /// <summary>空なのでリストが無いのを見た帯（リスト名）。対象品が戻ったら作り直しを頼んで外す。</summary>
    private readonly HashSet<string> emptyWithoutList = new(StringComparer.Ordinal);

    /// <summary>キャラクターが変わったら覚えを捨てる（リスト名にはキャラクターが入らないため）。</summary>
    public void Clear()
    {
        this.removeRequested.Clear();
        this.emptyWithoutList.Clear();
    }

    /// <summary>
    /// 作り直しを頼む帯を 1 つ決める（作り直しはその機能の全部の帯をまとめて直すので、1 回の確認で頼むのは 1 つまで）。
    /// 帯は前から順に見て、頼む帯が決まったらそこで止める（残りの帯は次の確認で見る）。
    /// </summary>
    public (Action Action, string? Name) Decide(IEnumerable<Band> bands)
    {
        foreach (var b in bands)
        {
            var empty = b.Ungathered == 0 && b.Unknown == 0;
            if (!b.HasList)
            {
                if (empty)
                    this.emptyWithoutList.Add(b.Name);
                else if (b.Ungathered > 0 && this.emptyWithoutList.Remove(b.Name))
                    return (Action.Restore, b.Name);
                continue;
            }

            this.emptyWithoutList.Remove(b.Name);
            if (!empty)
            {
                this.removeRequested.Remove(b.Name);
                continue;
            }

            if (this.removeRequested.Add(b.Name))
                return (Action.Remove, b.Name);
        }

        return (Action.None, null);
    }
}
