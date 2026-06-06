using Microsoft.Extensions.Logging;

namespace TradingEngine.Services;

public sealed class PositionTracker(
    ISymbolInfoRegistry symbolRegistry,
    Func<string, string, decimal> crossRateProvider,
    IRiskManager riskManager,
    IPositionManager positionManager,
    PersistenceService persistence,
    IEngineClock clock,
    ILogger<PositionTracker> logger)
{
    private readonly Dictionary<Guid, OrderRequest> _pendingOrders = new();
    private readonly Dictionary<Guid, Position> _openPositions = new();
    private decimal _latestRiskAmount;

    public IReadOnlyDictionary<Guid, Position> OpenPositions => _openPositions;

    public IReadOnlyList<PositionModification> EvaluatePosition(Position position, Tick tick, IReadOnlyList<Bar> bars)
        => positionManager.Evaluate(position, tick, bars);

    public void TrackOrder(Guid orderId, OrderRequest request, decimal riskAmount)
    {
        _pendingOrders[orderId] = request;
        _latestRiskAmount = riskAmount;
    }

    public void OnExecution(ExecutionEvent evt, IEnumerable<IStrategy> strategies)
    {
        if (evt.NewState != OrderState.Filled || evt.FillPrice is null) return;
        var fillPrice = evt.FillPrice.Value.Value;

        if (!_pendingOrders.TryGetValue(evt.OrderId, out var order))
        {
            ClosePosition(evt, fillPrice, strategies);
            return;
        }

        _pendingOrders.Remove(evt.OrderId);
        var position = new Position(
            Guid.NewGuid(), evt.OrderId, order.Intent.Symbol, order.Intent.Direction,
            evt.FilledLots, new Price(fillPrice), order.Intent.StopLoss, order.Intent.TakeProfit,
            clock.UtcNow, order.Intent.StrategyId);

        _openPositions[evt.OrderId] = position;
        riskManager.RegisterPosition(position.Id, position.StrategyId, _latestRiskAmount);

        var posConfig = new PositionManagementConfig(
            position.StrategyId,
            new TrailingConfig(TrailingMethod.AtrMultiple, 0, 1.0, 1.0),
            true, 1.0, new Pips(1),
            new Money(_latestRiskAmount, "USD"));
        positionManager.RegisterPosition(position, posConfig);

        logger.LogInformation("Opened. Id={Id} Symbol={Symbol} Dir={Dir} Lots={Lots} Entry={Entry:F5}",
            position.Id, position.Symbol, position.Direction, position.Lots, position.EntryPrice.Value);
    }

    private void ClosePosition(ExecutionEvent evt, decimal fillPrice, IEnumerable<IStrategy> strategies)
    {
        if (!_openPositions.TryGetValue(evt.OrderId, out var pos)) return;

        var symbolInfo = symbolRegistry.Get(pos.Symbol);
        var pnl = PipCalculator.GrossPnL(pos.Direction, pos.EntryPrice, new Price(fillPrice), pos.Lots, symbolInfo, crossRateProvider);
        var exitReason = pos.Direction == TradeDirection.Long
            ? (fillPrice <= pos.CurrentStopLoss.Value ? "SL" : "TP")
            : (fillPrice >= pos.CurrentStopLoss.Value ? "SL" : "TP");

        _openPositions.Remove(evt.OrderId);
        riskManager.DeregisterPosition(pos.Id);
        positionManager.DeregisterPosition(pos.Id);

        foreach (var s in strategies.Where(s => s.Id == pos.StrategyId))
        {
            var tradeResult = new TradeResult(Guid.NewGuid(), pos.Id, pos.Symbol, pos.Direction, pos.Lots,
                pos.EntryPrice, new Price(fillPrice), pos.CurrentStopLoss, pos.TakeProfit,
                pos.OpenedAtUtc, clock.UtcNow, pnl, Money.Zero(pnl.Currency), Money.Zero(pnl.Currency),
                pnl, new Pips(0), 0, new Pips(0), new Pips(0),
                exitReason, pos.StrategyId, "standard", EngineMode.Backtest);
            s.OnTradeResult(tradeResult);
            _ = persistence.SaveTradeAsync(tradeResult, CancellationToken.None);
        }

        logger.LogInformation("Closed. Id={Id} Exit={Exit:F5} PnL={PnL:F2} Reason={Reason}", pos.Id, fillPrice, pnl.Amount, exitReason);
    }
}
