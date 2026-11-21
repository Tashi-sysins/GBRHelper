using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace GBRHelper.Navigation;

/// <summary>
/// 移動の記録（navigation-*.jsonl）を書く。1 ファイルが上限の大きさに達したら次のファイルへ移り、
/// ファイルの数が上限に達したら古いものから消して書き続ける（容量は「数×大きさ」で頭打ち）。
/// 前の版は 16 ファイルで書くのをやめ、プラグインを読み込み直すまで何も残らなかった（10-12 09:00 以降の記録が無い）。
/// 他のゲームが書いている途中のファイルは消せない（開いたまま）ので飛ばし、次に古いものを消す。
/// </summary>
public sealed class NavigationTraceWriter : IDisposable
{
    public const int DefaultMaxFiles = 16;
    public const long DefaultMaxFileBytes = 4 * 1024 * 1024;
    private const int MaxEntryBytes = 512 * 1024;
    private sealed record Entry(DateTimeOffset Time, string Kind, RouteRequest? Request, object? Detail);
    private readonly Channel<Entry> queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(128)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task writer;
    private readonly string directory;
    private readonly int maxFiles;
    private readonly long maxFileBytes;
    private long dropped, deleted;
    private string path;
    public string Path => Volatile.Read(ref path);
    public string Error { get; private set; } = "";
    public long Dropped => Interlocked.Read(ref dropped);
    /// <summary>古い記録として消したファイルの数（この書き手が消したもの）。</summary>
    public long Deleted => Interlocked.Read(ref deleted);
    public Task Completion => writer;

    public NavigationTraceWriter(string directory, int maxFiles = DefaultMaxFiles, long maxFileBytes = DefaultMaxFileBytes)
    {
        this.directory = directory;
        this.maxFiles = Math.Max(2, maxFiles);
        this.maxFileBytes = Math.Max(1024, maxFileBytes);
        path = NewPath();
        writer = Task.Run(Write);
    }
    public void Record(string kind, RouteRequest? request, object? detail = null)
    {
        if (!queue.Writer.TryWrite(new(DateTimeOffset.UtcNow, kind, request, detail))) Interlocked.Increment(ref dropped);
    }
    private string NewPath()
        => System.IO.Path.Combine(directory, "navigation-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".jsonl");

    /// <summary>新しいファイルを作る前に、数が上限未満になるまで古いものから消す。開いたまま（書いている途中）のものは飛ばす。</summary>
    private void Prune()
    {
        var files = new DirectoryInfo(directory).GetFiles("navigation-*.jsonl")
            .OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal).ToList();
        var excess = files.Count - (maxFiles - 1);
        foreach (var file in files)
        {
            if (excess <= 0) break;
            try { file.Delete(); excess--; Interlocked.Increment(ref deleted); }
            catch (IOException) { } // 他のゲームが書いている途中
            catch (UnauthorizedAccessException) { }
        }
    }
    private FileStream Open(string target)
    {
        Directory.CreateDirectory(directory);
        Prune();
        // 削除の共有を許さない：書いている途中のファイルを他のゲームの Prune が消さないように。
        return new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, true);
    }
    private async Task Write()
    {
        FileStream? file = null;
        try
        {
            file = Open(Path);
            var options = new JsonSerializerOptions { IncludeFields = true };
            long bytes = 0;
            await foreach (var item in queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item, options) + "\n");
                if (data.Length > Math.Min(MaxEntryBytes, maxFileBytes)) { Interlocked.Increment(ref dropped); continue; }
                if (bytes + data.Length > maxFileBytes)
                {
                    // 上限の大きさに達した：次のファイルへ移る（ここで一番古いファイルが消える）。
                    await file.DisposeAsync().ConfigureAwait(false);
                    var next = NewPath();
                    Volatile.Write(ref path, next);
                    file = Open(next);
                    bytes = 0;
                }
                await file.WriteAsync(data).ConfigureAwait(false); bytes += data.Length;
                await file.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) { Error = ex.GetType().Name + ": " + ex.Message; queue.Writer.TryComplete(); }
        finally { if (file != null) await file.DisposeAsync().ConfigureAwait(false); }
    }
    public void Dispose() { queue.Writer.TryComplete(); } // no synchronous disk wait on the framework thread
}
