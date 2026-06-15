using System.Threading.Channels;

namespace TradingEngine.Host;

/// <summary>
/// The engine's run logic, independent of the hosting model. <see cref="EngineWorker"/> is a thin
/// <c>BackgroundService</c> that just calls <see cref="RunAsync"/>; tests can call it directly.
///
/// It owns the four collaborators it composes — <see cref="IndicatorSnapshotService"/>,
/// <see cref="MarketEventSource"/>, <see cref="AccountProcessor"/> and <see cref="TradingLoop"/> —
/// and keeps as fields only the dependencies its own methods touch; everything else is consumed
/// locally during composition. (Previously EngineWorker held ~28 injected fields, five of them dead.)
/// </summary>
public sealed class EngineRunner
{
    private readonly IBrokerAdapter _broker;
    private readonly IRiskManager _riskManager;
    private readonly IReadOnlyList<IStrategy> _strategies;
    private readonly IEngineClock _clock;
    private readonly DataFeedService? _dataFeed;
    private readonly ISignalGate? _signalGate;
    private readonly EngineMode _engineMode;
    private readonly EngineRunContext _runContext;
    private readonly CrossRateStore _crossRateStore;
    private readonly IProgress<BacktestProgressEvent>? _progress;
    private readonly PositionTracker _positionTracker;
    private readonly Microsoft.Extensions.Logging.ILogger _logger;

    private readonly IndicatorSnapshotService _indicatorSnapshot;
    private readonly MarketEventSource _marketEvents;
    private readonly AccountProcessor _accountProcessor;
    private readonly TradingLoop _tradingLoop;

    private readonly Channel<ExecutionEvent> _executionEventChannel =
        Channel.CreateBounded<ExecutionEvent>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = true
        });

    private EquitySnapshot _currentEquity = new(DateTime.MinValue, 0, 0, 0, 0, 0, 0, 0, EngineMode.Backtest);
    private long _tickCount;

    public EngineRunner(
        EngineWorkerDependencies deps,
        EngineRunContext runContext,
        Microsoft.Extensions.Logging.ILogger logger)
    {
        _broker = deps.Market.Broker;
        _riskManager = deps.Risk.RiskManager;
        _strategies = deps.Strategies.Strategies.ToList();
        _clock = deps.Market.Clock;
        _dataFeed = deps.Market.DataFeed;
        _signalGate = deps.Strategies.SignalGate;
        _engineMode = deps.Market.EngineMode;
        _runContext = runContext;
        _crossRateStore = deps.Market.CrossRateStore;
        _progress = deps.Persistence.Progress;
        _positionTracker = deps.Strategies.PositionTracker;
        _logger = logger;

        _indicatorSnapshot = new IndicatorSnapshotService(deps.Market.Indicators, _strategies);
        _marketEvents = new MarketEventSource(_broker, _executionEventChannel, logger);
        _accountProcessor = new AccountProcessor(
            _riskManager, _positionTracker, deps.Risk.SizingPolicy, deps.Persistence.EventBus,
            _clock, _engineMode, _crossRateStore, deps.Persistence.EquitySink,
            e => Volatile.Write(ref _currentEquity, e), logger);
        _tradingLoop = new TradingLoop(
            _broker, _indicatorSnapshot, _marketEvents, deps.Strategies.OrderDispatcher, _positionTracker,
            deps.Strategies.StrategyBank, _strategies, deps.Strategies.RegimeDetector, _signalGate,
            deps.Market.SymbolRegistry, deps.Persistence.EventBus, _clock, runContext,
            () => Volatile.Read(ref _currentEquity), _progress, deps.Persistence.Journal, logger);
    }

    private void ResetState()
    {
        _indicatorSnapshot.Reset();
        _tradingLoop.Reset();
        Volatile.Write(ref _currentEquity, new EquitySnapshot(DateTime.MinValue, 0, 0, 0, 0, 0, 0, 0, _engineMode));
        Interlocked.Exchange(ref _tickCount, 0);
        _logger.LogInformation("Engine state reset for new connection");
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("Engine starting. Mode={Mode} Strategies={Count}",
            _engineMode, _strategies.Count);

        if (_signalGate is not null)
        {
            foreach (var s in _strategies)
                _signalGate.RegisterStrategy(s.Config);
        }

        if (_broker is CTraderBrokerAdapter mqAdapter)
            mqAdapter.OnConnected = ResetState;

        await _broker.ConnectAsync(ct);

        try
        {
            var accountState = await _broker.GetAccountStateAsync(ct);
            if (accountState.Balance > 0)
            {
                _riskManager.UpdateEquityLevels(accountState.Equity);
                _logger.LogInformation("Startup reconciliation: Balance={Balance} Equity={Equity} OpenPositions={Count}",
                    accountState.Balance, accountState.Equity, accountState.OpenPositions.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup reconciliation failed — continuing with clean state");
        }

        await _indicatorSnapshot.WarmUpIndicatorsAsync(ct);

        if (_engineMode == EngineMode.Backtest)
        {
            await RunBacktestLoopAsync(ct);
        }
        else
        {
            await Task.WhenAll(
                ProcessTicksAsync(ct),
                ProcessBarsAsync(ct),
                _marketEvents.ProcessAccountUpdatesAsync(ct),
                _marketEvents.ProcessExecutionEventsAsync(ct),
                _marketEvents.ProcessAccountQueueAsync(ct, _accountProcessor.HandleAsync));
        }

        _logger.LogInformation("Engine stopped. Ticks={Ticks} Bars={Bars}",
            Interlocked.Read(ref _tickCount), _tradingLoop.BarCount);
    }

    private async Task ProcessTicksAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogDebug("Tick processor started");
            await foreach (var tick in _broker.TickStream.ReadAllAsync(ct))
            {
                Interlocked.Increment(ref _tickCount);

                while (_executionEventChannel.Reader.TryRead(out var execEvent))
                {
                    await _positionTracker.OnExecutionAsync(execEvent, _strategies);
                    var state = execEvent.NewState;
                    _logger.LogInformation("EXEC|{OrderId}|{State}|fill={Fill}|lots={Lots}",
                        execEvent.OrderId, state,
                        execEvent.FillPrice?.Value.ToString("F5") ?? "none",
                        execEvent.FilledLots);

                    _progress?.Report(new BacktestProgressEvent(
                        _runContext.RunId, state == OrderState.Rejected ? "REJECTED" : "EXEC",
                        $"EXEC {execEvent.OrderId} [{state}] fill={execEvent.FillPrice?.Value.ToString("F5") ?? "none"} lots={execEvent.FilledLots}{(state == OrderState.Rejected ? " reason=" + (execEvent.RejectionReason ?? "?") : "")}",
                        _clock.UtcNow));
                }

                if (_dataFeed is not null && _broker is SimulatedBrokerAdapter sim)
                {
                    sim.OnTickReceived(tick);
                }

                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace("TICK|{Symbol}|{Bid:F5}|{Ask:F5}|{Total}",
                        tick.Symbol.Value, tick.Bid, tick.Ask, Interlocked.Read(ref _tickCount));
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            while (_executionEventChannel.Reader.TryRead(out var execEvent))
                await _positionTracker.OnExecutionAsync(execEvent, _strategies);
        }
        _logger.LogDebug("Tick processor stopped");
    }

    private async Task ProcessBarsAsync(CancellationToken ct)
    {
        _logger.LogDebug("Bar processor started");
        try
        {
            await foreach (var bar in _broker.BarStream.ReadAllAsync(ct))
            {
                try
                {
                    await _tradingLoop.ProcessBarAsync(bar, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "BAR_PROC_ERR|{Symbol}|{OpenTime}", bar.Symbol, bar.OpenTimeUtc);
                }
            }
        }
        catch (OperationCanceledException) { }
        _logger.LogDebug("Bar processor stopped");
    }

    private async Task RunBacktestLoopAsync(CancellationToken ct)
    {
        var initAcct = await _broker.AccountStream.ReadAsync(ct);
        await _accountProcessor.HandleAsync(initAcct);

        _logger.LogDebug("Backtest loop started");
        try
        {
            await foreach (var bar in _broker.BarStream.ReadAllAsync(ct))
            {
                try
                {
                    if (_broker is BacktestReplayAdapter replay)
                        replay.SyncToBar(bar.Close, bar.OpenTimeUtc);

                    UpdateCrossRates(bar);

                    // Pump every account update through the same processor the live path uses,
                    // so equity, the breach watchdog and daily/weekly/monthly resets all run.
                    while (_broker.AccountStream.TryRead(out var acctUpdate))
                        await _accountProcessor.HandleAsync(acctUpdate);

                    await _tradingLoop.ProcessBarAsync(bar, ct);

                    // Venue concern: a live broker fills SL/TP server-side; in backtest we
                    // simulate it against the bar range, then drain the resulting fills.
                    await SimulateBarExitsAsync(bar, ct);

                    if (_broker is CTraderBrokerAdapter netMq)
                        await _broker.CompleteBarAsync(netMq.CurrentBarSeq, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "BAR_PROC_ERR|{Symbol}|{OpenTime}", bar.Symbol, bar.OpenTimeUtc);
                }
            }
        }
        catch (OperationCanceledException) { }
        _logger.LogDebug("Backtest loop stopped");
    }

    private async Task SimulateBarExitsAsync(Bar bar, CancellationToken ct)
    {
        foreach (var (orderId, pos) in _positionTracker.OpenPositions.ToList())
        {
            if (pos.Symbol != bar.Symbol) continue;
            bool exit;
            if (pos.Direction == TradeDirection.Long)
            {
                exit = bar.Low <= pos.CurrentStopLoss.Value
                    || (pos.TakeProfit is not null && bar.High >= pos.TakeProfit.Value.Value);
            }
            else
            {
                exit = bar.High >= pos.CurrentStopLoss.Value
                    || (pos.TakeProfit is not null && bar.Low <= pos.TakeProfit.Value.Value);
            }

            if (exit)
            {
                _logger.LogInformation("BAR_EXIT|{Id}|{Symbol}|sl={SL:F5}|tp={TP}|low={Low:F5}|high={High:F5}",
                    orderId, pos.Symbol, pos.CurrentStopLoss.Value,
                    pos.TakeProfit?.Value ?? 0, bar.Low, bar.High);
                await _broker.ClosePositionAsync(orderId, ct);
            }
        }

        await _marketEvents.DrainExecutionStreamAsync(_positionTracker, _strategies, _progress, _runContext.RunId, _clock);
    }

    private void UpdateCrossRates(Bar bar)
    {
        if (bar.Symbol.Value == "GBPUSD") _crossRateStore.GbpUsdRate = bar.Close;
        else if (bar.Symbol.Value == "USDJPY") _crossRateStore.UsdJpyRate = bar.Close;
    }
}
