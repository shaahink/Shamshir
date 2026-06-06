namespace TradingEngine.Host;

public sealed class EngineWorker : BackgroundService
{
    private readonly IBrokerAdapter _broker;
    private readonly IRiskManager _riskManager;
    private readonly IPositionManager _positionManager;
    private readonly IEnumerable<IStrategy> _strategies;
    private readonly IIndicatorService _indicators;
    private readonly IEventBus _eventBus;
    private readonly IEngineClock _clock;
    private readonly DataFeedService? _dataFeed;
    private readonly ILogger<EngineWorker> _logger;

    private readonly Channel<ExecutionEvent> _executionEventChannel =
        Channel.CreateBounded<ExecutionEvent>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = true
        });

    private AccountUpdate? _latestAccountUpdate;

    public EngineWorker(
        IBrokerAdapter broker,
        IRiskManager riskManager,
        IPositionManager positionManager,
        IEnumerable<IStrategy> strategies,
        IIndicatorService indicators,
        IEventBus eventBus,
        IEngineClock clock,
        ILogger<EngineWorker> logger,
        DataFeedService? dataFeed = null)
    {
        _broker = broker;
        _riskManager = riskManager;
        _positionManager = positionManager;
        _strategies = strategies;
        _indicators = indicators;
        _eventBus = eventBus;
        _clock = clock;
        _dataFeed = dataFeed;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Engine starting. Strategy count: {Count}", _strategies.Count());

        await _broker.ConnectAsync(ct);
        await WarmUpIndicatorsAsync(ct);

        if (_dataFeed is not null)
        {
            _logger.LogInformation("Waiting for data feed to complete");
            await _dataFeed.FeedComplete;
            _logger.LogInformation("Data feed complete. Processing remaining ticks...");
        }

        var tasks = new[]
        {
            ProcessTicksAsync(ct),
            ProcessBarsAsync(ct),
            ProcessAccountUpdatesAsync(ct),
            ProcessExecutionEventsAsync(ct),
        };

        await Task.WhenAll(tasks);
        _logger.LogInformation("Engine stopped");
    }

    private async Task ProcessTicksAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var tick in _broker.TickStream.ReadAllAsync(ct))
            {
                while (_executionEventChannel.Reader.TryRead(out var execEvent))
                    HandleExecutionEvent(execEvent);

                var accountUpdate = Interlocked.Exchange(ref _latestAccountUpdate, null);
                if (accountUpdate is not null)
                    HandleAccountUpdate(accountUpdate);

                foreach (var strategy in _strategies)
                {
                    var context = new MarketContext(
                        tick.Symbol, tick,
                        new Dictionary<Timeframe, IReadOnlyList<Bar>>(),
                        new Dictionary<string, double>(),
                        _clock.UtcNow);

                    var intent = strategy.Evaluate(context);
                    if (intent is null) continue;

                    var equity = new EquitySnapshot(
                        _clock.UtcNow, 0, 0, 0, 0, 0, 0, 0, EngineMode.Backtest);
                    var violations = _riskManager.Validate(intent, equity);

                    if (violations.Count > 0)
                    {
                        _logger.LogWarning("Trade blocked. Strategy={Strategy} Symbol={Symbol} Violations={Violations}",
                            strategy.Id, intent.Symbol, string.Join(", ", violations.Select(v => v.Code)));
                        continue;
                    }

                    _logger.LogInformation("Trade opened. Strategy={Strategy} Symbol={Symbol} Direction={Dir} Lots={Lots}",
                        strategy.Id, intent.Symbol, intent.Direction, 0.1m);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessBarsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var _ in _broker.BarStream.ReadAllAsync(ct)) { }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessAccountUpdatesAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var update in _broker.AccountStream.ReadAllAsync(ct))
                Interlocked.Exchange(ref _latestAccountUpdate, update);
        }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessExecutionEventsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var evt in _broker.ExecutionStream.ReadAllAsync(ct))
                await _executionEventChannel.Writer.WriteAsync(evt, ct);
        }
        catch (OperationCanceledException) { }
    }

    private void HandleExecutionEvent(ExecutionEvent evt) { }
    private void HandleAccountUpdate(AccountUpdate update) { }

    private async Task WarmUpIndicatorsAsync(CancellationToken ct)
    {
        await Task.CompletedTask;
    }
}
