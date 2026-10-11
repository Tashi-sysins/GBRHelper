using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace GBRHelper.Navigation;

public sealed class NavigationTraceWriter : IDisposable
{
    private sealed record Entry(DateTimeOffset Time, string Kind, RouteRequest? Request, object? Detail);
    private readonly Channel<Entry> queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(128)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task writer;
    private long dropped;
    public string Path { get; }
    public string Error { get; private set; } = "";
    public long Dropped => Interlocked.Read(ref dropped);
    public Task Completion => writer;
    public NavigationTraceWriter(string directory)
    {
        Path = System.IO.Path.Combine(directory, "navigation-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".jsonl");
        writer = Task.Run(Write);
    }
    public void Record(string kind, RouteRequest? request, object? detail = null)
    {
        if (!queue.Writer.TryWrite(new(DateTimeOffset.UtcNow, kind, request, detail))) Interlocked.Increment(ref dropped);
    }
    private async Task Write()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var previous = new DirectoryInfo(System.IO.Path.GetDirectoryName(Path)!).GetFiles("navigation-*.jsonl");
            if (previous.Length >= 16) { Error = "診断の保存上限16ファイルです。必要な記録を退避してください"; queue.Writer.TryComplete(); return; }
            await using var file = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, true);
            var options = new JsonSerializerOptions { IncludeFields = true };
            long bytes = 0;
            await foreach (var item in queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item, options) + "\n");
                if (data.Length > 512 * 1024 || bytes + data.Length > 4 * 1024 * 1024) { Interlocked.Increment(ref dropped); continue; }
                await file.WriteAsync(data).ConfigureAwait(false); bytes += data.Length;
                await file.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) { Error = ex.GetType().Name + ": " + ex.Message; queue.Writer.TryComplete(); }
    }
    public void Dispose() { queue.Writer.TryComplete(); } // no synchronous disk wait on the framework thread
}
