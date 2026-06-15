namespace TradingEngine.Host;

/// <summary>
/// The single canonical per-bar trading body, extracted from <see cref="EngineWorker"/> so it is a
/// first-class unit that can be driven without an <c>IHost</c> / <c>BackgroundService</c>. Both the
/// live path and the backtest path call <see cref="ProcessBarAsync"/>; the only differences between
/// them (account pumping, SL/TP fill simulation, lock-step) live in <see cref="EngineWorker"/> around
/// this call. A test can construct a <c>TradingLoop</c> with the real risk pipeline and a fake broker
/// and assert on the resulting orders / drawdown / journal — see EngineHarnessBuilder.
///
/// Flow: indicator recompute → bar snapshot → regime → active strategies → evaluate → signal gate →
/// risk-validated dispatch → track order → drain executions.
/// </summary>
public sealed class TradingLoop(
    IBrokerAdapter broker,
    IndicatorSnapshotService indicatorSnapshot,
    MarketEventSource marketEvents,
    OrderDispatcher orderDispatcher,
    PositionTracker positionTracker,
    IStrategyBank strategyBank,
    IReadOnlyList<IStrategy> strategies,
    IRegimeDetector regimeDetector,
    ISignalGate? signalGate,
    ISymbolInfoRegistry symbolRegistry,
    IEventBus eventBus,
    IEngineClock clock,
    EngineRunContext runContext,
    Func<EquitySnapshot> currentEquity,
    IProgress<BacktestProgressEvent>? progress,
    IPipelineJournal? journal,
    Microsoft.Extensions.Logging.ILogger logger)
{
    private long _barCount;
    private readonly Dictionary<(Symbol, Timeframe), MarketRegime> _currentRegimes = new();

    public long BarCount => Interlocked.Read(ref _barCount);
    public IReadOnlyDictionary<(Symbol, Timeframe), MarketRegime> CurrentRegimes => _currentRegimes;

    public void Reset()
    {
        Interlocked.Exchange(ref _barCount, 0);
        _currentRegimes.Clear();
    }

    public async Task ProcessBarAsync(Bar bar, CancellationToken ct)
    {
        Interlocked.Increment(ref _barCount);
        var byTf = indicatorSnapshot.Bars.GetOrAdd(bar.Symbol, _ => new());
        var list = byTf.GetOrAdd(bar.Timeframe, _ => new());
        int barCount;
        lock (list)
        {
            list.Add(bar);
            if (list.Count > 500)
                list.RemoveAt(0);
            barCount = list.Count;
        }

        await indicatorSnapshot.RecomputeIndicatorsAsync(bar.Symbol, bar.Timeframe, ct);

        logger.LogDebug("BAR_EVAL|{Symbol}|{Tf}|openTime={OpenTime:yyyy-MM-dd HH:mm}|close={Close:F5}|bars={Count}|total={Total}",
            bar.Symbol.Value, bar.Timeframe, bar.OpenTimeUtc, bar.Close, barCount, Interlocked.Read(ref _barCount));

        progress?.Report(new BacktestProgressEvent(
            runContext.RunId, "BAR",
            $"Bar {bar.OpenTimeUtc:yyyy-MM-dd HH:mm} | close={bar.Close:F5} | total={Interlocked.Read(ref _barCount)}",
            clock.UtcNow));

        var halfSpread = ResolveHalfSpread(bar.Symbol);
        var closeTick = new Tick(bar.Symbol, bar.Close, bar.Close + halfSpread,
            bar.OpenTimeUtc + GetBarDuration(bar.Timeframe));
        var barSnapshot = indicatorSnapshot.BuildBarSnapshot(bar.Symbol);
        if (barSnapshot is null) return;

        indicatorSnapshot.BuildSharedIndicatorSnapshot(bar.Symbol);

        signalGate?.OnBar(bar.OpenTimeUtc);

        var regime = regimeDetector.Detect(bar.Symbol,
            barSnapshot[bar.Timeframe],
            indicatorSnapshot.ReusableIndicatorDict);
        _currentRegimes[(bar.Symbol, bar.Timeframe)] = regime;
        var activeStrategies = strategyBank.GetActive(bar.Symbol, bar.Timeframe, regime);

        foreach (var strategy in activeStrategies)
        {
            var totalBars = barSnapshot.Values.Sum(b => b.Count);
            var strategyIndicators = indicatorSnapshot.BuildStrategyIndicatorValues(bar.Symbol, strategy);

            if (totalBars < strategy.RequiredBarCount)
            {
                logger.LogDebug("EVAL|{Strategy}|{Symbol}|NEED_BARS|have={Have}|need={Need}",
                    strategy.Id, bar.Symbol.Value, totalBars, strategy.RequiredBarCount);
                _ = eventBus.PublishAsync(new BarEvaluated(
                    runContext.RunId, bar.Symbol, bar.Timeframe, bar.OpenTimeUtc,
                    strategy.Id, new Dictionary<string, double>(strategyIndicators),
                    false, null, $"not enough bars (have {totalBars}, need {strategy.RequiredBarCount})",
                    clock.UtcNow), CancellationToken.None);
                continue;
            }

            var context = new MarketContext(bar.Symbol, closeTick, barSnapshot,
                strategyIndicators, clock.UtcNow);
            var intent = strategy.Evaluate(context);

            _ = eventBus.PublishAsync(new BarEvaluated(
                runContext.RunId, bar.Symbol, bar.Timeframe, bar.OpenTimeUtc,
                strategy.Id, new Dictionary<string, double>(strategyIndicators),
                intent is not null, intent?.Direction,
                intent?.Reason ?? "no signal",
                clock.UtcNow), CancellationToken.None);

            if (intent is null)
            {
                logger.LogDebug("EVAL|{Strategy}|{Symbol}|NO_SIGNAL", strategy.Id, bar.Symbol.Value);
                continue;
            }

            if (signalGate is not null)
            {
                var gateResult = signalGate.Check(strategy.Id, intent.Symbol.Value, intent.Direction, bar.OpenTimeUtc);
                if (!gateResult.Allowed)
                {
                    logger.LogInformation("SIGNAL_GATED|{Strategy}|{Symbol}|{Reason}", strategy.Id, intent.Symbol.Value, gateResult.Reason);
                    _ = eventBus.PublishAsync(new BarEvaluated(
                        runContext.RunId, bar.Symbol, bar.Timeframe, bar.OpenTimeUtc,
                        strategy.Id, new Dictionary<string, double>(strategyIndicators),
                        false, intent.Direction,
                        $"REENTRY:{gateResult.Reason}",
                        clock.UtcNow), CancellationToken.None);
                    continue;
                }
            }

            logger.LogInformation("SIGNAL|{Strategy}|{Symbol}|{Dir}|sl={SL:F5}|tp={TP}",
                strategy.Id, bar.Symbol.Value, intent.Direction,
                intent.StopLoss.Value, intent.TakeProfit?.Value.ToString("F5") ?? "none");
            logger.LogInformation("SIGNAL_REASON|{Strategy}|{Reason}", strategy.Id, intent.Reason);

            progress?.Report(new BacktestProgressEvent(
                runContext.RunId, "SIGNAL",
                $"SIGNAL {strategy.Id} {intent.Direction} sl={intent.StopLoss.Value:F5} tp={intent.TakeProfit?.Value.ToString("F5") ?? "none"} reason={intent.Reason}",
                clock.UtcNow));

            var equity = currentEquity();
            if (equity.Balance == 0)
            {
                logger.LogWarning("DISPATCH_SKIP|{Strategy}|{Symbol}|reason=equity not initialized",
                    strategy.Id, bar.Symbol.Value);
                continue;
            }
            var orderCtx = await orderDispatcher.DispatchAsync(intent, equity, bar.Close, broker, [], ct);
            if (orderCtx is null) continue;

            var orderReq = new OrderRequest(intent, orderCtx.Lots, intent.Symbol,
                intent.Direction, OrderType.Market, intent.LimitPrice);
            positionTracker.TrackOrder(orderCtx.OrderId, orderReq, orderCtx.RiskAmount);

            logger.LogInformation("ORDER|{Strategy}|{OrderId}|{Dir}|lots={Lots}|entry={Entry:F5}",
                strategy.Id, orderCtx.OrderId, intent.Direction, orderCtx.Lots, bar.Close);

            progress?.Report(new BacktestProgressEvent(
                runContext.RunId, "ORDER",
                $"ORDER {strategy.Id} {intent.Direction} lots={orderCtx.Lots:F2} entry~{bar.Close:F5}",
                clock.UtcNow));
        }

        journal?.Write("BAR_EVAL", bar.Symbol.Value, bar.OpenTimeUtc);

        await marketEvents.DrainExecutionStreamAsync(positionTracker, strategies, progress, runContext.RunId, clock);
    }

    private decimal ResolveHalfSpread(Symbol symbol)
    {
        try { return symbolRegistry.Get(symbol).TypicalSpread / 2m; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ResolveHalfSpread failed for {Symbol} — using fallback 0.5pip", symbol);
            return 0.00005m;
        }
    }

    private static TimeSpan GetBarDuration(Timeframe tf) => tf switch
    {
        Timeframe.M1 => TimeSpan.FromMinutes(1),
        Timeframe.M5 => TimeSpan.FromMinutes(5),
        Timeframe.M15 => TimeSpan.FromMinutes(15),
        Timeframe.M30 => TimeSpan.FromMinutes(30),
        Timeframe.H1 => TimeSpan.FromHours(1),
        Timeframe.H4 => TimeSpan.FromHours(4),
        Timeframe.D1 => TimeSpan.FromDays(1),
        _ => TimeSpan.FromHours(1),
    };
}
