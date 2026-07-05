using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Infrastructure.Persistence;

public sealed class TradePersistenceHandler : IEventHandler<TradeClosed>, IAsyncDisposable
{
    private readonly PersistenceService _persistence;
    private readonly ILogger<TradePersistenceHandler> _logger;
    private readonly IRunDataCache? _cache;
    private readonly Channel<(TradeResult Trade, string RunId, string? ExcursionPathJson)> _channel =
        Channel.CreateBounded<(TradeResult, string, string?)>(new BoundedChannelOptions(1_000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false
        });
    private readonly Task _flushTask;
    private readonly CancellationTokenSource _cts = new();

    public TradePersistenceHandler(PersistenceService persistence, ILogger<TradePersistenceHandler> logger, IRunDataCache? runDataCache = null)
    {
        _persistence = persistence;
        _logger = logger;
        _cache = runDataCache;
        _flushTask = DrainAsync(_cts.Token);
    }

    public async Task HandleAsync(TradeClosed evt, CancellationToken ct)
    {
        await _channel.Writer.WriteAsync((evt.Result, evt.RunId, evt.ExcursionPathJson), ct);
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        await foreach (var (trade, runId, excursionPathJson) in _channel.Reader.ReadAllAsync(ct))
        {
            try
            {
                await _persistence.SaveTradeAsync(trade, runId, ct);
                _cache?.AppendTrade(runId, trade);
                _logger.LogDebug("TRADE_SAVED|{TradeId}|RunId={RunId}", trade.Id, runId);

                // P3.1: write-through the SAME channel/drain point as the trade itself, into a separate
                // TradeExcursions table (not a new TradeResult column). Null for any trade the venue didn't
                // record a path for (RecordExcursions off, or a venue that doesn't support it).
                if (excursionPathJson is not null)
                {
                    await _persistence.SaveExcursionAsync(runId, trade.PositionId, excursionPathJson, ct);
                    _logger.LogDebug("EXCURSION_SAVED|{PositionId}|RunId={RunId}", trade.PositionId, runId);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to persist trade {TradeId}", trade.Id);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _channel.Writer.Complete(); } catch (ChannelClosedException) { }
        _cts.Cancel();
        try { await _flushTask; } catch { }
        _cts.Dispose();
    }
}
