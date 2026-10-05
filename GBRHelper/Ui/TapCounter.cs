using System;

namespace GBRHelper.Ui;

/// <summary>
/// 続けて何回押されたかを数える。検証タブを出す合図（記録タブを 5 回続けてクリック）に使う。
/// 画面にもゲームにも触らないので、ゲーム無しで試せる。
///
/// 「続けて」＝前に押してから <see cref="MaxGap"/> 以内に次を押すこと。間が空いたら 1 回目から数え直す。
/// 決まった回数に届いたら true を返し、数を 0 に戻す（続けて押し続けても、5 回ごとにしか反応しない）。
/// </summary>
public sealed class TapCounter
{
    /// <summary>届いたら合図を出す回数。</summary>
    public const int Required = 5;

    /// <summary>「続けて」とみなす間隔。ふつうに押し直す速さで届くよう、少し長めに取る。</summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(1.5);

    private int count;
    private DateTime last = DateTime.MinValue;

    /// <summary>いまの回数（画面に出すため）。</summary>
    public int Count => this.count;

    /// <summary>1 回押された。決まった回数に届いたら true。</summary>
    public bool Tap(DateTime now)
    {
        this.count = now - this.last <= MaxGap ? this.count + 1 : 1;
        this.last = now;

        if (this.count < Required)
            return false;

        this.count = 0;
        this.last = DateTime.MinValue;
        return true;
    }
}
