using System.Text.Json;
using TradingEngine.Infrastructure.Transport.NetMq;

namespace TradingEngine.Tests.Simulation.Harness;

public sealed class SnapshotRecorderSession : IAsyncDisposable
{
    private readonly StreamWriter _writer;
    private readonly NetMqMessageTransport _transport;
    private readonly CancellationTokenSource _cts = new();

    public string SnapshotPath { get; }

    public SnapshotRecorderSession(string snapshotPath, NetMqMessageTransport transport)
    {
        SnapshotPath = snapshotPath;
        _transport = transport;
        _writer = new StreamWriter(snapshotPath, append: false);
    }

    public Task StartAsync(string symbol, string period, string runId)
    {
        var header = JsonSerializer.Serialize(new SnapshotHeader
        {
            Version = 1,
            Symbol = symbol,
            Period = period,
            RecordedAtUtc = DateTime.UtcNow,
            RunId = runId,
        });
        _writer.WriteLine(header);

        // Start background recording from router messages
        _ = Task.Run(async () =>
        {
            var seq = 0L;
            var startTime = DateTime.UtcNow;
            try
            {
                await foreach (var (identity, json) in _transport.RouterMessages.ReadAllAsync(_cts.Token))
                {
                    var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
                    await _writer.WriteLineAsync(JsonSerializer.Serialize(new SnapshotFrame
                    {
                        Seq = seq++,
                        ElapsedMs = (long)elapsed,
                        Json = json,
                    }));
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        });

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await Task.Delay(200);
        await _writer.DisposeAsync();
    }
}

public sealed class SnapshotHeader
{
    public int Version { get; set; }
    public string? Symbol { get; set; }
    public string? Period { get; set; }
    public DateTime RecordedAtUtc { get; set; }
    public string? RunId { get; set; }
}

public sealed class SnapshotFrame
{
    public long Seq { get; set; }
    public long ElapsedMs { get; set; }
    public string Json { get; set; } = "";
}
