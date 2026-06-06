namespace TradingEngine.Tests.Simulation.Harness;

public sealed class EngineTestHarness
{
    private readonly List<IStrategy> _strategies = [];
    private RiskProfile? _riskProfile;
    private string? _dataPath;
    private PropFirmRuleSet? _propFirmRules;
    private ISymbolInfoRegistry? _registry;

    private sealed record TrackedOrder(Guid OrderId, Symbol Symbol, TradeDirection Direction, decimal Lots, Price StopLoss, Price? TakeProfit, string StrategyId);
    private sealed record TrackedPosition(Guid OrderId, Symbol Symbol, TradeDirection Direction, decimal Lots, Price EntryPrice, Price StopLoss, Price? TakeProfit, DateTime OpenedAtUtc, string StrategyId);

    public static EngineTestHarness Create() => new();

    public EngineTestHarness WithStrategy(IStrategy strategy)
    {
        _strategies.Add(strategy);
        return this;
    }

    public EngineTestHarness WithRiskProfile(RiskProfile profile)
    {
        _riskProfile = profile;
        return this;
    }

    public EngineTestHarness WithHistoricalData(string path)
    {
        _dataPath = path;
        return this;
    }

    public EngineTestHarness WithPropFirmRules(PropFirmRuleSet ruleSet)
    {
        _propFirmRules = ruleSet;
        return this;
    }

    public EngineTestHarness WithSymbolRegistry(ISymbolInfoRegistry registry)
    {
        _registry = registry;
        return this;
    }

    public async Task<BacktestResult> RunBacktestAsync(
        DateTime from, DateTime to, CancellationToken ct = default)
    {
        var clock = new StubClock(from);
        var symbol = Symbol.Parse("EURUSD");
        var broker = new SimulatedBrokerAdapter();
        var dataProvider = new HistoricalDataProvider(_dataPath!);

        var registry = _registry ?? CreateDefaultRegistry();
        var symbolInfo = registry.Get(symbol);
        var trades = new List<TradeResult>();
        var equityCurve = new List<EquitySnapshot>();
        var bars = new List<Bar>();
        var pendingOrders = new List<TrackedOrder>();
        var openPositions = new Dictionary<Guid, TrackedPosition>();

        await dataProvider.SeekAsync(from, to, ct);

        await foreach (var bar in dataProvider.StreamBarsAsync(symbol, Timeframe.H1, ct))
        {
            bars.Add(bar);

            var halfSpread = 0.0001m;
            var ticks = new[]
            {
                new Tick(symbol, bar.Open, bar.Open + halfSpread, bar.OpenTimeUtc),
                new Tick(symbol, bar.High, bar.High + halfSpread, bar.OpenTimeUtc.AddMinutes(15)),
                new Tick(symbol, bar.Low, bar.Low + halfSpread, bar.OpenTimeUtc.AddMinutes(30)),
                new Tick(symbol, bar.Close, bar.Close + halfSpread, bar.OpenTimeUtc.AddMinutes(45)),
            };

            foreach (var tick in ticks)
            {
                broker.OnTickReceived(tick);
                clock.Advance(TimeSpan.FromMinutes(15));

                var pendingExecs = new List<ExecutionEvent>();
                while (broker.ExecutionStream.TryRead(out var execEvt))
                    pendingExecs.Add(execEvt);

                foreach (var execEvt in pendingExecs)
                {
                    if (execEvt.FillPrice is null || execEvt.NewState != OrderState.Filled) continue;
                    var fillPrice = execEvt.FillPrice.Value.Value;

                    var pendingOrder = pendingOrders.FirstOrDefault(o => o.OrderId == execEvt.OrderId);
                    if (pendingOrder is not null)
                    {
                        pendingOrders.Remove(pendingOrder);
                        var pos = new TrackedPosition(
                            execEvt.OrderId, pendingOrder.Symbol, pendingOrder.Direction,
                            execEvt.FilledLots, new Price(fillPrice),
                            pendingOrder.StopLoss, pendingOrder.TakeProfit,
                            clock.UtcNow, pendingOrder.StrategyId);
                        openPositions[execEvt.OrderId] = pos;
                    }
                    else if (openPositions.TryGetValue(execEvt.OrderId, out var existingPos))
                    {
                        var pnl = PipCalculator.GrossPnL(
                            existingPos.Direction, existingPos.EntryPrice, new Price(fillPrice),
                            existingPos.Lots, symbolInfo, (_, _) => 1);

                        var exitReason = existingPos.Direction == TradeDirection.Long
                            ? (fillPrice <= existingPos.StopLoss.Value ? "SL" : "TP")
                            : (fillPrice >= existingPos.StopLoss.Value ? "SL" : "TP");

                        trades.Add(new TradeResult(
                            Guid.NewGuid(), existingPos.OrderId, existingPos.Symbol,
                            existingPos.Direction, existingPos.Lots,
                            existingPos.EntryPrice, new Price(fillPrice),
                            existingPos.StopLoss, existingPos.TakeProfit,
                            existingPos.OpenedAtUtc, clock.UtcNow,
                            pnl, Money.Zero(pnl.Currency), Money.Zero(pnl.Currency),
                            pnl, new Pips(0), (double)(pnl.Amount / (pnl.Amount != 0 ? pnl.Amount : 1)),
                            new Pips(0), new Pips(0),
                            exitReason, existingPos.StrategyId, "standard", EngineMode.Backtest));

                        openPositions.Remove(execEvt.OrderId);
                    }
                }

                foreach (var strategy in _strategies)
                {
                    if (bars.Count < strategy.RequiredBarCount) continue;

                    var indicators = new Dictionary<string, double>
                    {
                        ["ATR_14"] = 0.0021,
                        ["EMA_50"] = 1.0800,
                    };

                    var context = new MarketContext(
                        symbol, tick,
                        new Dictionary<Timeframe, IReadOnlyList<Bar>> { [Timeframe.H1] = [.. bars] },
                        indicators, clock.UtcNow);

                    var intent = strategy.Evaluate(context);
                    if (intent is null) continue;

                    var lots = 0.1m;
                    var orderReq = new OrderRequest(intent, lots, intent.Symbol, intent.Direction, OrderType.Market, null);
                    var orderId = await broker.SubmitOrderAsync(orderReq, ct);
                    pendingOrders.Add(new TrackedOrder(orderId, intent.Symbol, intent.Direction, lots, intent.StopLoss, intent.TakeProfit, strategy.Id));
                }
            }

            equityCurve.Add(new EquitySnapshot(
                clock.UtcNow, 100_000, 0, 100_000, 100_000, 100_000, 0, 0, EngineMode.Backtest));
        }

        var maxDrawdown = ComputeMaxDrawdown(equityCurve);
        var totalPnl = trades.Sum(t => t.NetPnL.Amount);

        return new BacktestResult(
            totalPnl,
            maxDrawdown,
            trades.Count,
            trades.Count > 0 ? (double)trades.Count(t => t.NetPnL.Amount > 0) / trades.Count : 0,
            trades,
            []);
    }

    private static decimal ComputeMaxDrawdown(List<EquitySnapshot> curve)
    {
        if (curve.Count == 0) return 0;
        var peak = curve[0].Equity;
        var maxDd = 0m;
        foreach (var snap in curve)
        {
            if (snap.Equity > peak) peak = snap.Equity;
            var dd = peak > 0 ? (peak - snap.Equity) / peak : 0;
            if (dd > maxDd) maxDd = dd;
        }
        return maxDd;
    }

    private static ISymbolInfoRegistry CreateDefaultRegistry()
    {
        var reg = new SymbolInfoRegistry();
        reg.Register(new SymbolInfo(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m));
        return reg;
    }
}
