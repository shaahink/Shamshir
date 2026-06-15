using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Host;

public sealed class MarketEventSource
{
    private readonly IBrokerAdapter _broker;
    private readonly Channel<ExecutionEvent> _executionChannel;
    private AccountUpdate? _latestAccountUpdate;
    private readonly Microsoft.Extensions.Logging.ILogger _logger;

    public MarketEventSource(
        IBrokerAdapter broker,
        Channel<ExecutionEvent> executionChannel,
        Microsoft.Extensions.Logging.ILogger logger)
    {
        _broker = broker;
        _executionChannel = executionChannel;
        _logger = logger;
    }

    public AccountUpdate? LatestAccountUpdate => Interlocked.Exchange(ref _latestAccountUpdate, null);

    public async Task ProcessAccountUpdatesAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogDebug("Account update processor started");
            await foreach (var update in _broker.AccountStream.ReadAllAsync(ct))
                Interlocked.Exchange(ref _latestAccountUpdate, update);
        }
        catch (OperationCanceledException) { }
    }

    public async Task ProcessExecutionEventsAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogDebug("Execution event processor started");
            await foreach (var evt in _broker.ExecutionStream.ReadAllAsync(ct))
                await _executionChannel.Writer.WriteAsync(evt, ct);
        }
        catch (OperationCanceledException) { }
    }

    public async Task ProcessAccountQueueAsync(
        CancellationToken ct,
        Func<AccountUpdate, Task> handler)
    {
        while (!ct.IsCancellationRequested)
        {
            var accountUpdate = LatestAccountUpdate;
            if (accountUpdate is not null)
                await handler(accountUpdate);
            await Task.Delay(100, ct);
        }
    }

    public async Task DrainExecutionStreamAsync(
        PositionTracker positionTracker, IReadOnlyList<IStrategy> strategies,
        IProgress<BacktestProgressEvent>? progress, string runId, IEngineClock clock)
    {
        while (_executionChannel.Reader.TryRead(out var execEvent))
        {
            await positionTracker.OnExecutionAsync(execEvent, strategies);
            var state = execEvent.NewState;
            _logger.LogInformation("EXEC|{OrderId}|{State}|fill={Fill}|lots={Lots}",
                execEvent.OrderId, state,
                execEvent.FillPrice?.Value.ToString("F5") ?? "none",
                execEvent.FilledLots);

            progress?.Report(new BacktestProgressEvent(
                runId, state == OrderState.Rejected ? "REJECTED" : "EXEC",
                $"EXEC {execEvent.OrderId} [{state}] fill={execEvent.FillPrice?.Value.ToString("F5") ?? "none"} lots={execEvent.FilledLots}{(state == OrderState.Rejected ? " reason=" + (execEvent.RejectionReason ?? "?") : "")}",
                clock.UtcNow));
        }
        while (_broker.ExecutionStream.TryRead(out var execEvent))
        {
            await positionTracker.OnExecutionAsync(execEvent, strategies);
            var state = execEvent.NewState;
            _logger.LogInformation("EXEC|{OrderId}|{State}|fill={Fill}|lots={Lots}",
                execEvent.OrderId, state,
                execEvent.FillPrice?.Value.ToString("F5") ?? "none",
                execEvent.FilledLots);

            progress?.Report(new BacktestProgressEvent(
                runId, state == OrderState.Rejected ? "REJECTED" : "EXEC",
                $"EXEC {execEvent.OrderId} [{state}] fill={execEvent.FillPrice?.Value.ToString("F5") ?? "none"} lots={execEvent.FilledLots}{(state == OrderState.Rejected ? " reason=" + (execEvent.RejectionReason ?? "?") : "")}",
                clock.UtcNow));
        }
    }
}
