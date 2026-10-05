using System;
using System.Collections.Generic;

namespace GBRHelper.Features;

/// <summary>
/// 動作の記録。画面に出すためのもの。
///
/// ファイルには書かない。このプラグインは1キャラクターで動かす前提で、
/// 記録したいのは「いま何が起きているか」だけのため。
/// Dalamud のログ（/xllog）には流すので、後から追いたいときはそちらを見る。
/// </summary>
public sealed class RunLog
{
    /// <summary>画面に残す行数。</summary>
    private const int MaxInMemory = 200;

    private readonly List<string> lines = [];
    private readonly object gate = new();

    /// <summary>1行記録する。</summary>
    public void Write(string category, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} [{category}] {message}";

        lock (this.gate)
        {
            this.lines.Add(line);

            if (this.lines.Count > MaxInMemory)
                this.lines.RemoveRange(0, this.lines.Count - MaxInMemory);
        }

        Svc.Log.Information($"[{category}] {message}");
    }

    /// <summary>画面表示用に直近の行を返す。</summary>
    public List<string> Snapshot()
    {
        lock (this.gate)
            return [.. this.lines];
    }

    public void Clear()
    {
        lock (this.gate)
            this.lines.Clear();
    }
}
