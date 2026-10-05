using System;

namespace GBRHelper.Features;

/// <summary>GBR の精選通知の世代を管理する。OFF で破棄された通知を次の精選へ持ち越さない。</summary>
public sealed class ReductionWait
{
    private long generation;
    private DateTime started;
    private bool completed;
    public bool Waiting { get; private set; }

    public Action Begin(DateTime now)
    {
        var token = ++generation;
        started = now; completed = false; Waiting = true;
        return () => { if (Waiting && token == generation) completed = true; };
    }

    public void Cancel()
    {
        ++generation; Waiting = false; completed = false;
    }

    public bool Ready(DateTime now, bool taskBusy)
    {
        if (!Waiting) return false;
        if (now - started > TimeSpan.FromMinutes(4))
            throw new InvalidOperationException("精選の完了を確かめられません。停止して確認してください");
        return completed && !taskBusy;
    }
}
