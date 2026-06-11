using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace TradingEngine.Web.Services;

public sealed class BacktestJournal
{
    private readonly BacktestProgressStore _progressStore;

    public BacktestJournal(BacktestProgressStore progressStore)
    {
        _progressStore = progressStore;
    }

    public void Write(string runId, string eventType, string message, ConcurrentQueue<string>? logQueue = null)
    {
        var json = JsonSerializer.Serialize(new { eventType, message });
        _progressStore.GetWriter(runId).TryWrite(json);
        logQueue?.Enqueue($"[{DateTime.UtcNow:HH:mm:ss}] {eventType} {message}");
    }
}
