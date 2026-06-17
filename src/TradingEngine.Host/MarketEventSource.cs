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

    /// <summary>
    /// One-shot drain used by the <b>backtest</b> path: synchronously applies every pending
    /// execution (from the internal channel and the broker stream) to the position tracker.
    /// </summary>
    public async Task DrainExecutionStreamAsync(
        PositionTracker positionTracker, IReadOnlyList<IStrategy> strategies,
        IProgress<BacktestProgressEvent>? progress, string runId, IEngineClock clock)
    {
        while (_executionChannel.Reader.TryRead(out var execEvent))
            await ApplyExecAsync(execEvent, positionTracker, strategies, progress, runId, clock);
        while (_broker.ExecutionStream.TryRead(out var execEvent))
            await ApplyExecAsync(execEvent, positionTracker, strategies, progress, runId, clock);
    }

    /// <summary>
    /// The single serialized execution consumer for the <b>live</b> path: the ONLY place
    /// <see cref="PositionTracker"/> is mutated by executions while running, so its non-thread-safe
    /// state is never touched concurrently. Pairs with <see cref="ProcessExecutionEventsAsync"/>
    /// (the single writer of <c>_executionChannel</c>). Keeps the tick loop off the position state.
    /// </summary>
    public async Task ConsumeExecutionsAsync(
        CancellationToken ct, PositionTracker positionTracker, IReadOnlyList<IStrategy> strategies,
        IProgress<BacktestProgressEvent>? progress, string runId, IEngineClock clock)
    {
        try
        {
            _logger.LogDebug("Execution consumer started");
            await foreach (var execEvent in _executionChannel.Reader.ReadAllAsync(ct))
                await ApplyExecAsync(execEvent, positionTracker, strategies, progress, runId, clock);
        }
        catch (OperationCanceledException) { }
        _logger.LogDebug("Execution consumer stopped");
    }

    private async Task ApplyExecAsync(
        ExecutionEvent execEvent, PositionTracker positionTracker, IReadOnlyList<IStrategy> strategies,
        IProgress<BacktestProgressEvent>? progress, string runId, IEngineClock clock)
    {
        await positionTracker.OnExecutionAsync(execEvent, strategies);
        var state = execEvent.NewState;
        _logger.LogInformation("EXEC|{OrderId}|{State}|fill={Fill}|lots={Lots}",
            execEvent.OrderId, state,
            execEvent.FillPrice?.Value.ToString("F5") ?? "none",
            execEvent.FilledLots);

        // A cancellation (expired resting limit) is neither a fill nor a rejection — surface it as its
        // own event so it never inflates the "fills" funnel, and the reason (ENTRY_EXPIRED) is visible.
        var eventType = state switch
        {
            OrderState.Rejected => "REJECTED",
            OrderState.Cancelled => "ENTRY_EXPIRED",
            _ => "EXEC",
        };
        var detail = state switch
        {
            OrderState.Rejected => $" reason={execEvent.RejectionReason ?? "?"}",
            OrderState.Cancelled => $" reason={execEvent.RejectionReason ?? "ENTRY_EXPIRED"}",
            _ => "",
        };
        progress?.Report(new BacktestProgressEvent(
            runId, eventType,
            $"{eventType} {execEvent.OrderId} [{state}] fill={execEvent.FillPrice?.Value.ToString("F5") ?? "none"} lots={execEvent.FilledLots}{detail}",
            clock.UtcNow));
    }
}
