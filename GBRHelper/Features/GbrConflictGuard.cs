using System;
using System.Collections.Generic;
using GBRHelper.Ipc;

namespace GBRHelper.Features;

/// <summary>
/// GatherBuddyReborn の設定のうち、このプラグインの回収と競合するものを、
/// このプラグインが動いている間（GBR の自動採集が ON の間）は競合しない値に保つ。
/// 要望「GBR にも似た機能があり競合する可能性があるので、
/// このプラグインが ON のときは関連する設定を競合しない値にする」。
///
/// 【対象と理由】（GBR 7.5.6.1 のソースで確認）
///   Wait for AutoRetainer Multi-mode（AutoRetainerMultiMode）を OFF に保つ。
///     ON だとベンチャーの完了が近づいたとき、GBR が自分で採集を止め、AutoRetainer のマルチモードを
///     有効にして回収させる（AutoGather/AutoGather.cs:832-839, 2635-2720）。このプラグインの回収と取り合う。
///     GBR はディアデムに入るとこの設定を自分で OFF にし、出ると ON に戻す（:550-570）。
///     一度 OFF にするだけでは戻されるので、動いている間は見張り続ける。
///
/// 【対象にしなかったもの】
///   Go home when idle／Go home when done・Lifestream Command … Lifestream で家へ帰るだけで、リテイナーには触れない。
///     帰っている最中に回収が重なっても、宿屋へ向かう段（InnTrip）が Lifestream の作業の終わりを待つ（90 秒まで）。
///   Check Retainer Inventories … 所持数を数えるときにリテイナーの持ち物も足すだけ。
///
/// 【戻さない】このプラグインは GBR の自動採集と完全に連動していて、GBR が動く間はいつも動いている。
/// 止まったときに元へ戻すと、次に動いたときにまた書き換えることになるだけなので、戻さない。
/// 変えたときは必ず記録に残す（何をいくつからいくつにしたか）。
///
/// 【アンロード経路では触らない】読み直しのたびに GBR の設定を書き換えないよう、見張りは動いている間だけ。
/// </summary>
public sealed class GbrConflictGuard
{
    /// <summary>競合しない値に保つ設定1件。</summary>
    /// <param name="Property">GBR の AutoGatherConfig のプロパティ名（GatherBuddy/AutoGather/AutoGather.Config.cs）。</param>
    /// <param name="Required">保つ値。</param>
    /// <param name="Label">GBR の画面の表示（英語のまま）と訳。検索窓に英語を入れると見つかる。</param>
    /// <param name="Reason">なぜその値にするか。</param>
    public readonly record struct Rule(string Property, bool Required, string Label, string Reason);

    /// <summary>対象の設定。</summary>
    public static readonly Rule[] Rules =
    [
        new("AutoRetainerMultiMode", false,
            "Wait for AutoRetainer Multi-mode（AutoRetainer のマルチモードを待つ）",
            "GBR が自分で採集を止めて AutoRetainer に回収させるので、このプラグインの回収と取り合うため"),
    ];

    /// <summary>
    /// 読んだ値から、書き換えが要る設定を選ぶ。ゲームに触らないので、ゲーム無しで試せる。
    /// 読めない（null）ものは書き換えない（分からないまま書くと、GBR の版が変わったときに別のものを壊しうる）。
    /// </summary>
    public static List<Rule> NeedsChange(Func<string, bool?> read)
    {
        var list = new List<Rule>();

        foreach (var rule in Rules)
        {
            if (read(rule.Property) is { } current && current != rule.Required)
                list.Add(rule);
        }

        return list;
    }

    /// <summary>動いている間に確かめ直す間隔。GBR がディアデムの出入りで戻すことがあるため。</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);

    private readonly GbrConfigAccess access;
    private readonly RunLog log;

    private bool wasActive;
    private DateTime nextCheck = DateTime.MinValue;
    private string lastReportedError = string.Empty;

    public GbrConflictGuard(GbrConfigAccess access, RunLog log)
    {
        this.access = access;
        this.log = log;
    }

    /// <summary>直近に読んだ値（プロパティ名 → 値。読めなければ null）。画面に出す。</summary>
    public Dictionary<string, bool?> LastRead { get; } = [];

    /// <summary>直近の失敗。無ければ空。</summary>
    public string LastError => this.access.LastError;

    /// <summary>
    /// 毎フレーム呼ぶ。動き出した瞬間と、動いている間は一定の間隔で確かめる。
    /// </summary>
    /// <param name="active">このプラグインが動いているか（GBR の自動採集が ON）。</param>
    public void Tick(bool active)
    {
        if (!active)
        {
            this.wasActive = false;
            return;
        }

        var now = DateTime.UtcNow;

        // 動き出した瞬間はすぐ確かめる。GBR が先に AutoRetainer を待ち始めないように。
        if (this.wasActive && now < this.nextCheck)
            return;

        this.wasActive = true;
        this.nextCheck = now + CheckInterval;
        this.Enforce();
    }

    /// <summary>対象の設定を読み、競合する値なら書き換える。</summary>
    public void Enforce()
    {
        foreach (var rule in Rules)
            this.LastRead[rule.Property] = this.access.GetBool(rule.Property);

        foreach (var rule in NeedsChange(p => this.LastRead.GetValueOrDefault(p)))
        {
            var before = this.LastRead[rule.Property];

            if (this.access.SetBool(rule.Property, rule.Required))
            {
                this.LastRead[rule.Property] = rule.Required;
                this.log.Write("GBR",
                    $"GBR の設定「{rule.Label}」を {OnOff(before)} → {OnOff(rule.Required)} にしました（{rule.Reason}）");
            }
            else
            {
                this.ReportError($"GBR の設定「{rule.Label}」を {OnOff(rule.Required)} にできませんでした（{this.access.LastError}）");
            }
        }

        // 読めなかったものがあれば知らせる（同じ内容は繰り返さない）。
        if (!string.IsNullOrEmpty(this.access.LastError))
            this.ReportError($"GBR の設定を確かめられません（{this.access.LastError}）");
        else
            this.lastReportedError = string.Empty;
    }

    private void ReportError(string message)
    {
        if (message == this.lastReportedError)
            return;

        this.lastReportedError = message;
        this.log.Write("GBR", message);
    }

    public static string OnOff(bool? value) => value switch
    {
        true => "ON",
        false => "OFF",
        _ => "（読めない）",
    };
}
