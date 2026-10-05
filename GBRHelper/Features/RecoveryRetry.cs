using System;

namespace GBRHelper.Features;

/// <summary>復元が失敗しても毎フレーム保存し直さない。待ち時間は試行前に確定する。</summary>
public sealed class RecoveryRetry
{
    private DateTime nextAttempt;
    public bool TryBegin(DateTime now)
    {
        if (now < nextAttempt) return false;
        nextAttempt = now.AddSeconds(2);
        return true;
    }
}
