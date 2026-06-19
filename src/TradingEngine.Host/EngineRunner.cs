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
    private readonly IEnginePacer _pacer;
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
        _pacer = deps.Market.EngineMode == EngineMode.Backtest
            ? new BarSteppedPacer()
            : new AsyncStreamPacer();
        _logger = logger;

        _indicatorSnapshot = new IndicatorSnapshotService(deps.Market.Indicators, _strategies);
        _marketEvents = new MarketEventSource(_broker, _executionEventChannel, logger);
        _accountProcessor = new AccountProcessor(
            _riskManager, _positionTracker, deps.Risk.SizingPolicy, deps.Persistence.EventBus,
            _clock, _engineMode, _crossRateStore, deps.Persistence.EquitySink,
            e => Volatile.Write(ref _currentEquity, e), logger,
            progress: _progress, runId: runContext.RunId);
        _tradingLoop = new TradingLoop(
            _broker, _indicatorSnapshot, deps.Strategies.OrderDispatcher, _positionTracker,
            deps.Strategies.StrategyBank, deps.Strategies.RegimeDetector, _signalGate,
            deps.Risk.Governor,
            deps.Market.SymbolRegistry, deps.Persistence.EventBus, _clock, runContext,
            _crossRateStore.Convert,
            () => Volatile.Read(ref _currentEquity), _progress, deps.Persistence.Journal,
            deps.Strategies.EntryPlanner, logger);
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

        _broker.RegisterConnectedHandler(ResetState);

        // V1/V2 — when the venue reports its open positions (connect + every reconnect), seed the
        // tracker so the engine can manage/trail/force-close positions that already exist at the
        // venue after a restart. Idempotent: SeedOpenPositions skips positions already tracked.
        _broker.RegisterReconcileHandler(state =>
        {
            if (state.Balance > 0)
            {
                _riskManager.UpdateEquityLevels(state.Equity);
            }
            _positionTracker.SeedOpenPositions(state.OpenPositions, _strategies);
        });

        // V3 — write venue-confirmed SL/TP modifications back onto the tracked position.
        _broker.RegisterStopModifiedHandler((orderId, sl, tp) =>
            _positionTracker.ConfirmStopLoss(orderId, sl, tp));

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

        await _pacer.PaceAsync(this, ct);

        _logger.LogInformation("Engine stopped. Ticks={Ticks} Bars={Bars}",
            Interlocked.Read(ref _tickCount), _tradingLoop.BarCount);
    }

    internal async Task ProcessTicksAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogDebug("Tick processor started");
            // Lean hot path: translate ticks only. Execution fills are handled by the dedicated
            // ConsumeExecutionsAsync consumer — the tick loop never touches PositionTracker.
            await foreach (var tick in _broker.TickStream.ReadAllAsync(ct))
            {
                Interlocked.Increment(ref _tickCount);

                if (_dataFeed is not null)
                {
                    _broker.OnTickObserved(tick);
                }

                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace("TICK|{Symbol}|{Bid:F5}|{Ask:F5}|{Total}",
                        tick.Symbol.Value, tick.Bid, tick.Ask, Interlocked.Read(ref _tickCount));
                }
            }
        }
        catch (OperationCanceledException) { }
        _logger.LogDebug("Tick processor stopped");
    }

    internal async Task ProcessBarsAsync(CancellationToken ct)
    {
        _logger.LogDebug("Bar processor started");
        try
        {
            await foreach (var bar in _broker.BarStream.ReadAllAsync(ct))
            {
                try
                {
                    await _tradingLoop.ProcessBarAsync(bar, ct);
                    // Live venues fill SL/TP server-side; manage breakeven/trailing for open positions
                    // each bar so the venue stop is ratcheted up.
                    await _tradingLoop.UpdateTrailingStopsAsync(bar, ct);
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

    internal async Task RunBacktestLoopAsync(CancellationToken ct)
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
                    _broker.OnBarObserved(bar);

                    UpdateCrossRates(bar);

                    // Pump every account update through the same processor the live path uses,
                    // so equity, the breach watchdog and daily/weekly/monthly resets all run.
                    while (_broker.AccountStream.TryRead(out var acctUpdate))
                        await _accountProcessor.HandleAsync(acctUpdate);

                    await _tradingLoop.ProcessBarAsync(bar, ct);

                    // Entry fills: the loop no longer drains internally, so drain this bar's
                    // dispatched executions before evaluating SL/TP exits.
                    await _marketEvents.DrainExecutionStreamAsync(_positionTracker, _strategies, _progress, _runContext.RunId, _clock);

                    // Venue concern: a live broker fills SL/TP server-side; in backtest we
                    // simulate it against the bar range, then drain the resulting fills.
                    await SimulateBarExitsAsync(bar, ct);

                    // Manage still-open positions (breakeven/trailing) AFTER the exit check, so a moved
                    // stop only affects the next bar (no intrabar look-ahead).
                    await _tradingLoop.UpdateTrailingStopsAsync(bar, ct);

                    await _broker.CompleteBarAsync(ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "BAR_PROC_ERR|{Symbol}|{OpenTime}", bar.Symbol, bar.OpenTimeUtc);
                }
            }

            // F1 (iter-26): the last bar's exits emit account updates that the top-of-loop drain
            // never reaches (the foreach has ended). Drain them so the final trade's realized
            // PnL/drawdown reaches the processor and the equity curve closes on the true balance.
            while (_broker.AccountStream.TryRead(out var tail))
                await _accountProcessor.HandleAsync(tail);
        }
        catch (OperationCanceledException) { }
        _logger.LogDebug("Backtest loop stopped");
    }

    /// <summary>
    /// (iter-35/A2 DEPRECATED): replaced by kernel EngineReducer.HandleBarClosed + DetectSlTpExit.
    /// The kernel path owns SL/TP detection as part of the pure reducer. This method remains only
    /// until the full kernel cutover wires EngineRunner through KernelDriver — then DELETE.
    /// </summary>
    internal async Task SimulateBarExitsAsync(Bar bar, CancellationToken ct)
    {
        foreach (var (orderId, pos) in _positionTracker.OpenPositions.ToList())
        {
            if (pos.Symbol != bar.Symbol) continue;
            string? reason = null;
            Price exitPrice = pos.CurrentStopLoss;
            if (pos.Direction == TradeDirection.Long)
            {
                if (bar.Low <= pos.CurrentStopLoss.Value) { reason = "SL"; exitPrice = pos.CurrentStopLoss; }
                else if (pos.TakeProfit is not null && bar.High >= pos.TakeProfit.Value.Value) { reason = "TP"; exitPrice = pos.TakeProfit.Value; }
            }
            else
            {
                if (bar.High >= pos.CurrentStopLoss.Value) { reason = "SL"; exitPrice = pos.CurrentStopLoss; }
                else if (pos.TakeProfit is not null && bar.Low <= pos.TakeProfit.Value.Value) { reason = "TP"; exitPrice = pos.TakeProfit.Value; }
            }

            if (reason is not null)
            {
                _logger.LogInformation("BAR_EXIT|{Id}|{Symbol}|reason={Reason}|sl={SL:F5}|tp={TP}|low={Low:F5}|high={High:F5}",
                    orderId, pos.Symbol, reason, pos.CurrentStopLoss.Value,
                    pos.TakeProfit?.Value ?? 0, bar.Low, bar.High);
                // Stamp WHY before asking the venue to close, so the close fill records the real
                // exit reason (SL/TP) in the journal/ledger instead of the generic "FORCE".
                _positionTracker.SetCloseReason(orderId, reason);
                // F2/D3: fill at the stop/target price, not the bar close.
                await _broker.ClosePositionAtAsync(orderId, exitPrice, ct);
            }
        }

        await _marketEvents.DrainExecutionStreamAsync(_positionTracker, _strategies, _progress, _runContext.RunId, _clock);
    }

    internal Task ProcessAccountUpdatesAsync(CancellationToken ct)
        => _marketEvents.ProcessAccountUpdatesAsync(ct);

    internal Task ProcessExecutionEventsAsync(CancellationToken ct)
        => _marketEvents.ProcessExecutionEventsAsync(ct);

    internal Task ConsumeExecutionsAsync(CancellationToken ct)
        => _marketEvents.ConsumeExecutionsAsync(ct, _positionTracker, _strategies, _progress, _runContext.RunId, _clock);

    internal Task ProcessAccountQueueAsync(CancellationToken ct)
        => _marketEvents.ProcessAccountQueueAsync(ct, _accountProcessor.HandleAsync);

    private void UpdateCrossRates(Bar bar)
    {
        if (bar.Symbol.Value == "GBPUSD") _crossRateStore.GbpUsdRate = bar.Close;
        else if (bar.Symbol.Value == "USDJPY") _crossRateStore.UsdJpyRate = bar.Close;
    }
}
