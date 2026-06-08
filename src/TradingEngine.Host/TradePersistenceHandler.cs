using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Host;

public sealed class TradePersistenceHandler : IEventHandler<TradeClosed>, IAsyncDisposable
{
    private readonly PersistenceService _persistence;
    private readonly ILogger<TradePersistenceHandler> _logger;
    private readonly Channel<(TradeResult Trade, string RunId)> _channel =
        Channel.CreateBounded<(TradeResult, string)>(new BoundedChannelOptions(1_000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false
        });
    private readonly Task _flushTask;
    private readonly CancellationTokenSource _cts = new();

    public TradePersistenceHandler(PersistenceService persistence, ILogger<TradePersistenceHandler> logger)
    {
        _persistence = persistence;
        _logger = logger;
        _flushTask = DrainAsync(_cts.Token);
    }

    public async Task HandleAsync(TradeClosed evt, CancellationToken ct)
    {
        await _channel.Writer.WriteAsync((evt.Result, evt.RunId), ct);
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        await foreach (var (trade, runId) in _channel.Reader.ReadAllAsync(ct))
        {
            try
            {
                await _persistence.SaveTradeAsync(trade, runId, ct);
                _logger.LogDebug("TRADE_SAVED|{TradeId}|RunId={RunId}", trade.Id, runId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to persist trade {TradeId}", trade.Id);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.Complete();
        _cts.Cancel();
        try { await _flushTask; } catch { }
        _cts.Dispose();
    }
}
