using Microsoft.Extensions.Logging;

namespace TradingEngine.Services;

public sealed class PositionTracker(
    ISymbolInfoRegistry symbolRegistry,
    Func<string, string, decimal> crossRateProvider,
    IRiskManager riskManager,
    IPositionManager positionManager,
    IEventBus eventBus,
    EngineRunContext runContext,
    IEngineClock clock,
    ILogger<PositionTracker> logger,
    EngineMode engineMode = EngineMode.Backtest,
    ITradingGovernor? governor = null,
    ISignalGate? signalGate = null)
{
    private readonly Dictionary<Guid, (OrderRequest Request, decimal FilledLots)> _pendingOrders = new();
    private readonly Dictionary<Guid, Position> _openPositions = new();
    private readonly Dictionary<Guid, (decimal RiskAmount, string RiskProfileId)> _pendingRisk = new();
    private readonly HashSet<Guid> _processedExecutionIds = [];

    public IReadOnlyDictionary<Guid, Position> OpenPositions => _openPositions;

    public IReadOnlyList<PositionModification> EvaluatePosition(Position position, Tick tick, IReadOnlyList<Bar> bars)
        => positionManager.Evaluate(position, tick, bars);

    public void TrackOrder(Guid orderId, OrderRequest request, decimal riskAmount, string? riskProfileId = null)
    {
        _pendingOrders[orderId] = (request, 0m);
        _pendingRisk[orderId] = (riskAmount, riskProfileId ?? "standard");
    }

    public async Task OnExecutionAsync(ExecutionEvent evt, IEnumerable<IStrategy> strategies)
    {
        if (_processedExecutionIds.Contains(evt.OrderId) && !_openPositions.ContainsKey(evt.OrderId))
        {
            logger.LogWarning("Duplicate execution event skipped. OrderId={OrderId}", evt.OrderId);
            return;
        }
        _processedExecutionIds.Add(evt.OrderId);

        if (evt.NewState == OrderState.Rejected)
        {
            _pendingOrders.Remove(evt.OrderId);
            _pendingRisk.Remove(evt.OrderId);
            logger.LogWarning("Order rejected. Id={Id} Reason={Reason}", evt.OrderId, evt.RejectionReason ?? "unknown");
            return;
        }

        if (evt.NewState != OrderState.Filled || evt.FillPrice is null) return;
        var fillPrice = evt.FillPrice.Value.Value;

        if (!_pendingOrders.TryGetValue(evt.OrderId, out var entry))
        {
            if (_openPositions.TryGetValue(evt.OrderId, out var openPos)
                && evt.FilledLots > 0 && evt.FilledLots < openPos.Lots - 0.0001m)
            {
                await HandlePartialCloseAsync(evt, fillPrice, openPos, strategies);
                return;
            }
            await ClosePositionAsync(evt, fillPrice, strategies);
            return;
        }

        var (order, currentFilled) = entry;
        var newFilled = currentFilled + evt.FilledLots;

        if (newFilled >= order.Lots)
        {
            _pendingOrders.Remove(evt.OrderId);
            _pendingRisk.Remove(evt.OrderId);
        }
        else
        {
            _pendingOrders[evt.OrderId] = (order, newFilled);
            logger.LogInformation("Partial fill. OrderId={OrderId} Filled={Filled}/{Requested}", evt.OrderId, newFilled, order.Lots);
            return;
        }

        var position = new Position(
            Guid.NewGuid(), evt.OrderId, order.Intent.Symbol, order.Intent.Direction,
            order.Lots, new Price(fillPrice), order.Intent.StopLoss, order.Intent.TakeProfit,
            clock.UtcNow, order.Intent.StrategyId);

        _openPositions[evt.OrderId] = position;
        var (riskAmount, riskProfileId) = _pendingRisk.GetValueOrDefault(evt.OrderId, (0m, "standard"));
        riskManager.RegisterPosition(position.Id, position.StrategyId, riskAmount);

        var posConfig = new PositionManagementConfig(
            position.StrategyId,
            new TrailingConfig(TrailingMethod.AtrMultiple, 0, 1.0, 1.0),
            true, 1.0, new Pips(1),
            new Money(riskAmount, "USD"));
        positionManager.RegisterPosition(position, posConfig);

        signalGate?.OnPositionOpened(position.StrategyId, position.Symbol.Value, position.Direction, clock.UtcNow);

        logger.LogInformation("Opened. Id={Id} Symbol={Symbol} Dir={Dir} Lots={Lots} Entry={Entry:F5}",
            position.Id, position.Symbol, position.Direction, position.Lots, position.EntryPrice.Value);
    }

    private async Task ClosePositionAsync(ExecutionEvent evt, decimal fillPrice, IEnumerable<IStrategy> strategies)
    {
        if (!_openPositions.TryGetValue(evt.OrderId, out var pos)) return;

        var symbolInfo = symbolRegistry.Get(pos.Symbol);
        var pnl = PipCalculator.GrossPnL(pos.Direction, pos.EntryPrice, new Price(fillPrice), pos.Lots, symbolInfo, crossRateProvider);
        var exitReason = DetermineExitReason(pos, fillPrice);

        _openPositions.Remove(evt.OrderId);
        _processedExecutionIds.Remove(evt.OrderId);
        riskManager.DeregisterPosition(pos.Id);
        positionManager.DeregisterPosition(pos.Id);

        var (_, riskProfileId) = _pendingRisk.GetValueOrDefault(evt.OrderId, (0m, "standard"));

        TradeResult tradeResult = default!;
        foreach (var s in strategies.Where(s => s.Id == pos.StrategyId))
        {
            tradeResult = new TradeResult(Guid.NewGuid(), pos.Id, pos.Symbol, pos.Direction, pos.Lots,
                pos.EntryPrice, new Price(fillPrice), pos.CurrentStopLoss, pos.TakeProfit,
                pos.OpenedAtUtc, clock.UtcNow, pnl, Money.Zero(pnl.Currency), Money.Zero(pnl.Currency),
                pnl, new Pips(0), 0, new Pips(0), new Pips(0),
                exitReason, pos.StrategyId, riskProfileId, engineMode);
            s.OnTradeResult(tradeResult);
            await eventBus.PublishAsync(new TradeClosed(tradeResult, runContext.RunId, clock.UtcNow), CancellationToken.None);
        }

        governor?.OnTradeClosed(tradeResult);
        signalGate?.OnPositionClosed(pos.StrategyId, pos.Symbol.Value, pos.Direction, exitReason, clock.UtcNow);

        logger.LogInformation("Closed. Id={Id} Exit={Exit:F5} PnL={PnL:F2} Reason={Reason}", pos.Id, fillPrice, pnl.Amount, exitReason);
    }

    private async Task HandlePartialCloseAsync(ExecutionEvent evt, decimal fillPrice, Position pos, IEnumerable<IStrategy> strategies)
    {
        var closedLots = evt.FilledLots;
        var remainingLots = pos.Lots - closedLots;

        var symbolInfo = symbolRegistry.Get(pos.Symbol);
        var pnl = PipCalculator.GrossPnL(pos.Direction, pos.EntryPrice, new Price(fillPrice), closedLots, symbolInfo, crossRateProvider);

        var reducedPosition = pos with { Lots = remainingLots };
        _openPositions[evt.OrderId] = reducedPosition;

        var (riskAmount, riskProfileId) = _pendingRisk.GetValueOrDefault(evt.OrderId, (0m, "standard"));
        var proportionalRisk = pos.Lots > 0 ? riskAmount * remainingLots / pos.Lots : 0m;
        riskManager.RegisterPosition(pos.Id, pos.StrategyId, proportionalRisk);

        await eventBus.PublishAsync(new PositionPartiallyClosed(
            pos.Id, closedLots, remainingLots, fillPrice, clock.UtcNow), CancellationToken.None);

        logger.LogInformation("Partially closed. Id={Id} Closed={ClosedLots} Remaining={RemainingLots} PnL={PnL:F2}",
            pos.Id, closedLots, remainingLots, pnl.Amount);
    }

    private static string DetermineExitReason(Position pos, decimal fillPrice)
    {
        if (pos.Direction == TradeDirection.Long)
        {
            if (fillPrice <= pos.CurrentStopLoss.Value) return "SL";
            if (pos.TakeProfit is not null && fillPrice >= pos.TakeProfit.Value.Value) return "TP";
            return "FORCE";
        }
        if (fillPrice >= pos.CurrentStopLoss.Value) return "SL";
        if (pos.TakeProfit is not null && fillPrice <= pos.TakeProfit.Value.Value) return "TP";
        return "FORCE";
    }
}
