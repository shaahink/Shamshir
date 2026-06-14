using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Infrastructure.Persistence;

public sealed class BarEvaluationHandler : IEventHandler<BarEvaluated>, IAsyncDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BarEvaluationHandler> _logger;
    private readonly Channel<BarEvaluated> _channel =
        Channel.CreateBounded<BarEvaluated>(new BoundedChannelOptions(50_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false
        });
    private readonly Task _flushTask;
    private readonly CancellationTokenSource _cts = new();

    public BarEvaluationHandler(IServiceScopeFactory scopeFactory, ILogger<BarEvaluationHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _flushTask = FlushLoopAsync(_cts.Token);
    }

    public Task HandleAsync(BarEvaluated evt, CancellationToken ct)
    {
        _channel.Writer.TryWrite(evt);
        return Task.CompletedTask;
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        var buffer = new List<BarEvaluated>(500);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(3_000, ct);
                buffer.Clear();
                while (_channel.Reader.TryRead(out var evt) && buffer.Count < 500)
                    buffer.Add(evt);
                if (buffer.Count == 0) continue;

                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                foreach (var evt in buffer)
                {
                    db.BarEvaluations.Add(new BarEvaluationEntity
                    {
                        Id = Guid.NewGuid(),
                        RunId = evt.RunId,
                        Symbol = evt.Symbol.Value,
                        Timeframe = evt.Timeframe.ToString(),
                        BarOpenTimeUtc = evt.BarOpenTimeUtc,
                        StrategyId = evt.StrategyId,
                        IndicatorValuesJson = JsonSerializer.Serialize(evt.IndicatorValues),
                        SignalFired = evt.SignalFired,
                        SignalDirection = evt.SignalDirection?.ToString(),
                        Reason = evt.Reason,
                        OccurredAtUtc = evt.OccurredAtUtc,
                    });
                }
                await db.SaveChangesAsync(ct);
                _logger.LogDebug("BAR_EVAL_FLUSHED|{Count}", buffer.Count);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BarEvaluationHandler flush failed");
            }
        }
    }

    public async Task FlushRemainingAsync()
    {
        var remaining = new List<BarEvaluated>(1_000);
        while (_channel.Reader.TryRead(out var evt))
            remaining.Add(evt);

        if (remaining.Count == 0) return;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            foreach (var evt in remaining)
            {
                db.BarEvaluations.Add(new BarEvaluationEntity
                {
                    Id = Guid.NewGuid(),
                    RunId = evt.RunId,
                    Symbol = evt.Symbol.Value,
                    Timeframe = evt.Timeframe.ToString(),
                    BarOpenTimeUtc = evt.BarOpenTimeUtc,
                    StrategyId = evt.StrategyId,
                    IndicatorValuesJson = JsonSerializer.Serialize(evt.IndicatorValues),
                    SignalFired = evt.SignalFired,
                    SignalDirection = evt.SignalDirection?.ToString(),
                    Reason = evt.Reason,
                    OccurredAtUtc = evt.OccurredAtUtc,
                });
            }
            await db.SaveChangesAsync(CancellationToken.None);
            _logger.LogDebug("BarEvaluationHandler: explicit pre-dispose flush {Count}", remaining.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BarEvaluationHandler: pre-dispose flush failed");
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
