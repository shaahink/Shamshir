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
    private EngineState _state = EngineState.Empty;
    private readonly HashSet<Guid> _processedExecutionIds = [];
    private readonly Dictionary<Guid, (OrderRequest Request, decimal RiskAmount, string RiskProfileId)> _pendingIntent = new();

    public IReadOnlyDictionary<Guid, Position> OpenPositions
    {
        get
        {
            var result = new Dictionary<Guid, Position>();
            foreach (var (_, ps) in _state.Positions)
            {
                if (ps.Phase is PositionPhase.Open or PositionPhase.Reducing or PositionPhase.Closing)
                {
                    result[ps.OrderId] = ToPosition(ps);
                }
            }
            return result;
        }
    }

    public IReadOnlyList<PositionModification> EvaluatePosition(Position position, Tick tick, IReadOnlyList<Bar> bars)
        => positionManager.Evaluate(position, tick, bars);

    public void TrackOrder(Guid orderId, OrderRequest request, decimal riskAmount, string? riskProfileId = null)
    {
        _pendingIntent[orderId] = (request, riskAmount, riskProfileId ?? "standard");

        var posState = PositionLifecycle.CreateIntended(
            orderId, request.Intent.Symbol, request.Intent.Direction,
            request.Lots, request.Intent.LimitPrice, request.Intent.StopLoss,
            request.Intent.TakeProfit, request.Intent.StrategyId);

        var submitted = new OrderSubmitted(orderId, request.Intent.Symbol, request.Intent.Direction,
            request.Lots, request.Intent.LimitPrice, request.Intent.StrategyId, clock.UtcNow);

        var decision = EngineReducer.Apply(_state, submitted);
        _state = decision.State;
    }

    public async Task OnExecutionAsync(ExecutionEvent evt, IEnumerable<IStrategy> strategies)
    {
        if (_processedExecutionIds.Contains(evt.OrderId) && !_state.Positions.Any(kv => kv.Value.OrderId == evt.OrderId))
        {
            logger.LogWarning("Duplicate execution event skipped. OrderId={OrderId}", evt.OrderId);
            return;
        }
        _processedExecutionIds.Add(evt.OrderId);

        var symbol = GetSymbolForOrder(evt.OrderId);
        var engineEvent = evt.NewState switch
        {
            OrderState.Rejected => (EngineEvent)new OrderRejected(evt.OrderId, symbol, evt.RejectionReason ?? "unknown", evt.TimestampUtc),
            OrderState.Filled when evt.FillPrice is not null => new OrderFilled(evt.OrderId, symbol, evt.FilledLots, evt.FillPrice ?? new Price(0), evt.TimestampUtc),
            OrderState.PartiallyFilled when evt.FillPrice is not null => new OrderPartiallyFilled(evt.OrderId, symbol, evt.FilledLots, evt.FillPrice ?? new Price(0), evt.TimestampUtc),
            _ => (EngineEvent)new OrderFilled(evt.OrderId, symbol, evt.FilledLots, evt.FillPrice ?? new Price(0), evt.TimestampUtc)
        };

        var beforePhase = FindPhase(evt.OrderId);
        var decision = EngineReducer.Apply(_state, engineEvent);
        var afterPhase = FindPhaseIn(decision.State, evt.OrderId);
        _state = decision.State;

        if (afterPhase == PositionPhase.Rejected)
        {
            _pendingIntent.Remove(evt.OrderId);
            logger.LogWarning("Order rejected. Id={Id} Reason={Reason}", evt.OrderId, evt.RejectionReason ?? "unknown");
            return;
        }

        if (beforePhase == PositionPhase.Intended || beforePhase == PositionPhase.Submitted)
        {
            if (afterPhase == PositionPhase.Open)
            {
                OnOpened(evt, strategies);
            }
            else if (afterPhase == PositionPhase.Submitted)
            {
                logger.LogInformation("Partial fill. OrderId={OrderId} Filled={Filled}", evt.OrderId, evt.FilledLots);
            }
            return;
        }

        var fillPrice = evt.FillPrice?.Value ?? 0;

        if (afterPhase == PositionPhase.Reducing)
        {
            await HandlePartialCloseAsync(evt, fillPrice, strategies);
            return;
        }

        if (afterPhase == PositionPhase.Closed)
        {
            await ClosePositionAsync(evt, fillPrice, strategies);
            return;
        }
    }

    private void OnOpened(ExecutionEvent evt, IEnumerable<IStrategy> strategies)
    {
        var ps = _state.Positions.Values.FirstOrDefault(p => p.OrderId == evt.OrderId);
        if (ps is null) return;

        var (request, riskAmount, riskProfileId) = _pendingIntent.GetValueOrDefault(evt.OrderId,
            (default!, 0m, "standard"));

        var position = new Position(
            Guid.NewGuid(), evt.OrderId, ps.Symbol, ps.Direction,
            ps.Lots, ps.EntryPrice, ps.CurrentStopLoss, ps.TakeProfit,
            clock.UtcNow, ps.StrategyId);

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
        var ps = _state.Positions.Values.FirstOrDefault(p => p.OrderId == evt.OrderId);
        if (ps is null && !TryBuildPosition(evt.OrderId, fillPrice, out var pos)) return;

        Position position;
        if (ps is not null)
        {
            position = ToPosition(ps);
        }
        else
        {
            pos = default!;
            return;
        }

        var symbolInfo = symbolRegistry.Get(position.Symbol);
        var pnl = PipCalculator.GrossPnL(position.Direction, position.EntryPrice, new Price(fillPrice), position.Lots, symbolInfo, crossRateProvider);
        var exitReason = DetermineExitReason(position, fillPrice);

        _processedExecutionIds.Remove(evt.OrderId);
        riskManager.DeregisterPosition(position.Id);
        positionManager.DeregisterPosition(position.Id);

        var (_, _, riskProfileId) = _pendingIntent.GetValueOrDefault(evt.OrderId, (default!, 0m, "standard"));

        TradeResult tradeResult = default!;
        foreach (var s in strategies.Where(s => s.Id == position.StrategyId))
        {
            tradeResult = new TradeResult(Guid.NewGuid(), position.Id, position.Symbol, position.Direction, position.Lots,
                position.EntryPrice, new Price(fillPrice), position.CurrentStopLoss, position.TakeProfit,
                position.OpenedAtUtc, clock.UtcNow, pnl, Money.Zero(pnl.Currency), Money.Zero(pnl.Currency),
                pnl, new Pips(0), 0, new Pips(0), new Pips(0),
                exitReason, position.StrategyId, riskProfileId);
            s.OnTradeResult(tradeResult);
            await eventBus.PublishAsync(new TradeClosed(tradeResult, runContext.RunId, clock.UtcNow), CancellationToken.None);
        }

        governor?.OnTradeClosed(tradeResult);
        signalGate?.OnPositionClosed(position.StrategyId, position.Symbol.Value, position.Direction, exitReason, clock.UtcNow);

        logger.LogInformation("Closed. Id={Id} Exit={Exit:F5} PnL={PnL:F2} Reason={Reason}", position.Id, fillPrice, pnl.Amount, exitReason);
    }

    private async Task HandlePartialCloseAsync(ExecutionEvent evt, decimal fillPrice, IEnumerable<IStrategy> strategies)
    {
        var ps = _state.Positions.Values.FirstOrDefault(p => p.OrderId == evt.OrderId);
        if (ps is null) return;

        var position = ToPosition(ps);
        var closedLots = evt.FilledLots;
        var remainingLots = ps.Lots;

        var symbolInfo = symbolRegistry.Get(position.Symbol);
        var pnl = PipCalculator.GrossPnL(position.Direction, position.EntryPrice, new Price(fillPrice), closedLots, symbolInfo, crossRateProvider);

        var (_, riskAmount, riskProfileId) = _pendingIntent.GetValueOrDefault(evt.OrderId, (default!, 0m, "standard"));
        var proportionalRisk = position.Lots > 0 ? riskAmount * remainingLots / position.Lots : 0m;
        riskManager.RegisterPosition(position.Id, position.StrategyId, proportionalRisk);

        await eventBus.PublishAsync(new PositionPartiallyClosed(
            position.Id, closedLots, remainingLots, fillPrice, clock.UtcNow), CancellationToken.None);

        logger.LogInformation("Partially closed. Id={Id} Closed={ClosedLots} Remaining={RemainingLots} PnL={PnL:F2}",
            position.Id, closedLots, remainingLots, pnl.Amount);
    }

    private Symbol GetSymbolForOrder(Guid orderId)
    {
        var ps = _state.Positions.Values.FirstOrDefault(p => p.OrderId == orderId);
        if (ps is not null) return ps.Symbol;
        if (_pendingIntent.TryGetValue(orderId, out var entry))
            return entry.Request.Intent.Symbol;
        return Symbol.Parse("EURUSD");
    }

    private PositionPhase? FindPhase(Guid orderId)
    {
        return _state.Positions.Values.FirstOrDefault(p => p.OrderId == orderId)?.Phase;
    }

    private static PositionPhase? FindPhaseIn(EngineState state, Guid orderId)
    {
        return state.Positions.Values.FirstOrDefault(p => p.OrderId == orderId)?.Phase;
    }

    private static Position ToPosition(PositionState ps)
    {
        return new Position(
            ps.PositionId, ps.OrderId, ps.Symbol, ps.Direction,
            ps.Lots, ps.EntryPrice, ps.CurrentStopLoss, ps.TakeProfit,
            ps.OpenedAtUtc == DateTime.MinValue ? DateTime.UtcNow : ps.OpenedAtUtc, ps.StrategyId);
    }

    private bool TryBuildPosition(Guid orderId, decimal fillPrice, out Position position)
    {
        position = default!;
        return false;
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
