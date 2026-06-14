using Microsoft.Extensions.Logging;
using TradingEngine.Engine;

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
    ITradingGovernor? governor = null,
    ISignalGate? signalGate = null)
{
    private readonly Dictionary<Guid, (OrderRequest Request, decimal FilledLots)> _pendingOrders = new();
    private readonly Dictionary<Guid, Position> _openPositions = new();
    private readonly Dictionary<Guid, (decimal RiskAmount, string RiskProfileId)> _pendingRisk = new();
    private readonly HashSet<Guid> _processedExecutionIds = [];
    private readonly Dictionary<Guid, PositionState> _lifecycleStates = new();

    public IReadOnlyDictionary<Guid, Position> OpenPositions => _openPositions;

    public IReadOnlyList<PositionModification> EvaluatePosition(Position position, Tick tick, IReadOnlyList<Bar> bars)
        => positionManager.Evaluate(position, tick, bars);

    public void TrackOrder(Guid orderId, OrderRequest request, decimal riskAmount, string? riskProfileId = null)
    {
        _pendingOrders[orderId] = (request, 0m);
        _pendingRisk[orderId] = (riskAmount, riskProfileId ?? "standard");

        var posState = PositionLifecycle.CreateIntended(
            orderId, request.Intent.Symbol, request.Intent.Direction,
            request.Lots, request.Intent.LimitPrice, request.Intent.StopLoss,
            request.Intent.TakeProfit, request.Intent.StrategyId);

        var submitted = new OrderSubmitted(orderId, request.Intent.Symbol, request.Intent.Direction,
            request.Lots, request.Intent.LimitPrice, request.Intent.StrategyId, clock.UtcNow);
        var (nextState, _) = PositionLifecycle.Apply(posState, submitted);
        _lifecycleStates[orderId] = nextState;
    }

    public async Task OnExecutionAsync(ExecutionEvent evt, IEnumerable<IStrategy> strategies)
    {
        if (_processedExecutionIds.Contains(evt.OrderId) && !_openPositions.ContainsKey(evt.OrderId))
        {
            logger.LogWarning("Duplicate execution event skipped. OrderId={OrderId}", evt.OrderId);
            return;
        }
        _processedExecutionIds.Add(evt.OrderId);

        var engineEvent = ToEngineEvent(evt);
        if (!_lifecycleStates.TryGetValue(evt.OrderId, out var posState))
        {
            if (evt.NewState != OrderState.Filled || evt.FillPrice is null) return;
            await ClosePositionAsync(evt, evt.FillPrice.Value.Value, strategies);
            return;
        }

        var (nextState, effects) = PositionLifecycle.Apply(posState, engineEvent);
        _lifecycleStates[evt.OrderId] = nextState;

        switch (nextState.Phase)
        {
            case PositionPhase.Rejected:
                _pendingOrders.Remove(evt.OrderId);
                _pendingRisk.Remove(evt.OrderId);
                _lifecycleStates.Remove(evt.OrderId);
                logger.LogWarning("Order rejected. Id={Id} Reason={Reason}", evt.OrderId, nextState.RejectionReason);
                break;

            case PositionPhase.Submitted:
                // Partial fill during entry — update pending orders
                if (_pendingOrders.TryGetValue(evt.OrderId, out var entry))
                {
                    if (nextState.FilledLots >= entry.Request.Lots)
                    {
                        _pendingOrders.Remove(evt.OrderId);
                        _pendingRisk.Remove(evt.OrderId);
                    }
                    else
                    {
                        _pendingOrders[evt.OrderId] = (entry.Request, nextState.FilledLots);
                    }
                }
                break;

            case PositionPhase.Open:
                OpenPosition(evt, nextState);
                break;

            case PositionPhase.Reducing:
                if (_openPositions.TryGetValue(evt.OrderId, out var openPos))
                {
                    await HandlePartialCloseAsync(evt, evt.FillPrice?.Value ?? 0, openPos, strategies);
                }
                break;

            case PositionPhase.Closed:
                await ClosePositionAsync(evt, evt.FillPrice?.Value ?? 0, strategies);
                break;

            case PositionPhase.Closing:
                break;
        }
    }

    private EngineEvent ToEngineEvent(ExecutionEvent evt)
    {
        var symbol = GetSymbolForOrder(evt.OrderId);

        return evt.NewState switch
        {
            OrderState.Rejected => new OrderRejected(evt.OrderId, symbol, evt.RejectionReason ?? "unknown", evt.TimestampUtc),
            OrderState.Filled when evt.FillPrice is not null => new OrderFilled(evt.OrderId, symbol, evt.FilledLots, evt.FillPrice ?? new Price(0), evt.TimestampUtc),
            OrderState.PartiallyFilled when evt.FillPrice is not null => new OrderPartiallyFilled(evt.OrderId, symbol, evt.FilledLots, evt.FillPrice ?? new Price(0), evt.TimestampUtc),
            _ => new OrderFilled(evt.OrderId, symbol, evt.FilledLots, evt.FillPrice ?? new Price(0), evt.TimestampUtc)
        };
    }

    private Symbol GetSymbolForOrder(Guid orderId)
    {
        if (_pendingOrders.TryGetValue(orderId, out var entry))
            return entry.Request.Intent.Symbol;
        if (_openPositions.TryGetValue(orderId, out var pos))
            return pos.Symbol;
        if (_lifecycleStates.TryGetValue(orderId, out var lc))
            return lc.Symbol;
        return Symbol.Parse("EURUSD");
    }

    private void OpenPosition(ExecutionEvent evt, PositionState state)
    {
        if (_openPositions.ContainsKey(evt.OrderId)) return;

        if (!_pendingOrders.TryGetValue(evt.OrderId, out var entry))
        {
            var position = new Position(
                Guid.NewGuid(), evt.OrderId, state.Symbol, state.Direction,
                state.Lots, state.EntryPrice, state.CurrentStopLoss, state.TakeProfit,
                clock.UtcNow, state.StrategyId);
            _openPositions[evt.OrderId] = position;
            return;
        }

        var (order, _) = entry;
        var fillPrice = evt.FillPrice?.Value ?? 0;
        var position2 = new Position(
            Guid.NewGuid(), evt.OrderId, order.Intent.Symbol, order.Intent.Direction,
            order.Lots, new Price(fillPrice), order.Intent.StopLoss, order.Intent.TakeProfit,
            clock.UtcNow, order.Intent.StrategyId);

        _openPositions[evt.OrderId] = position2;
        var (riskAmount2, riskProfileId2) = _pendingRisk.GetValueOrDefault(evt.OrderId, (0m, "standard"));
        riskManager.RegisterPosition(position2.Id, position2.StrategyId, riskAmount2);

        var posConfig = new PositionManagementConfig(
            position2.StrategyId,
            new TrailingConfig(TrailingMethod.AtrMultiple, 0, 1.0, 1.0),
            true, 1.0, new Pips(1),
            new Money(riskAmount2, "USD"));
        positionManager.RegisterPosition(position2, posConfig);

        signalGate?.OnPositionOpened(position2.StrategyId, position2.Symbol.Value, position2.Direction, clock.UtcNow);

        logger.LogInformation("Opened. Id={Id} Symbol={Symbol} Dir={Dir} Lots={Lots} Entry={Entry:F5}",
            position2.Id, position2.Symbol, position2.Direction, position2.Lots, position2.EntryPrice.Value);
    }

    private async Task ClosePositionAsync(ExecutionEvent evt, decimal fillPrice, IEnumerable<IStrategy> strategies)
    {
        if (!_openPositions.TryGetValue(evt.OrderId, out var pos)) return;

        var symbolInfo = symbolRegistry.Get(pos.Symbol);
        var pnl = PipCalculator.GrossPnL(pos.Direction, pos.EntryPrice, new Price(fillPrice), pos.Lots, symbolInfo, crossRateProvider);
        var exitReason = DetermineExitReason(pos, fillPrice);

        _openPositions.Remove(evt.OrderId);
        _processedExecutionIds.Remove(evt.OrderId);
        _lifecycleStates.Remove(evt.OrderId);
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
                exitReason, pos.StrategyId, riskProfileId);
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
