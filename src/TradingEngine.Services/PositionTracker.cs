using Microsoft.Extensions.Logging;
using TradingEngine.Engine;

namespace TradingEngine.Services;

public sealed class PositionTracker(
    ISymbolInfoRegistry symbolRegistry,
    Func<string, string, decimal> crossRateProvider,
    IRiskManager riskManager,
    IPositionManager positionManager,
    IEventBus eventBus,
    IEngineClock clock,
    ILogger<PositionTracker> logger,
    IEffectExecutor? effectExecutor = null,
    ISignalGate? signalGate = null)
{
    private EngineState _state = EngineState.Empty;
    private readonly HashSet<Guid> _processedExecutionIds = [];
    // F8 (iter-26): last applied execution signature per order, to drop EXACT duplicate fills
    // (same state/price/lots/timestamp) the venue may resend. A genuine close differs from the
    // entry in price/timestamp, so this never swallows a real close — unlike a lots-only check,
    // which can't tell a duplicate full-lots entry fill from a full-lots close.
    private readonly Dictionary<Guid, (OrderState State, decimal? Price, decimal Lots, DateTime Ts)> _lastExecSig = new();
    private readonly Dictionary<Guid, (OrderRequest Request, decimal RiskAmount, string RiskProfileId)> _pendingIntent = new();

    // Serializes the three mutating entry points (TrackOrder, OnExecutionAsync,
    // RequestForceCloseAllAsync) so this tracker's non-thread-safe state is only ever touched from
    // one thread at a time. In live mode those run on different tasks (bar loop, execution consumer,
    // account/breach task); in single-threaded backtest the lock is uncontended. EffectExecutor does
    // not call back into this type, so there is no re-entrancy/deadlock.
    private readonly SemaphoreSlim _mutex = new(1, 1);

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
        _mutex.Wait();
        try
        {
            _pendingIntent[orderId] = (request, riskAmount, riskProfileId ?? "standard");

            var submitted = new OrderSubmitted(orderId, request.Intent.Symbol, request.Intent.Direction,
                request.Lots, request.Intent.LimitPrice, request.Intent.StrategyId, clock.UtcNow,
                request.Intent.StopLoss, request.Intent.TakeProfit);

            var decision = EngineReducer.Apply(_state, submitted);
            _state = decision.State;
        }
        finally { _mutex.Release(); }
    }

    /// <summary>
    /// V1/V2 — seed the tracker from the venue's open-position snapshot (startup/reconnect
    /// reconciliation). For each venue position the engine is not already tracking, drives the
    /// lifecycle FSM (Submitted → Filled) so it lands in the Open phase and registers it with the
    /// position manager and risk manager. Idempotent: positions already tracked (matched by the
    /// engine OrderId/clientOrderId) are skipped, so repeated reconnects don't duplicate.
    /// </summary>
    public void SeedOpenPositions(IReadOnlyList<OpenPositionInfo> venuePositions, IEnumerable<IStrategy> strategies)
    {
        _mutex.Wait();
        try
        {
            foreach (var info in venuePositions)
            {
                if (_state.Positions.Values.Any(p => p.OrderId == info.PositionId))
                {
                    continue; // already tracking this position — resync is a no-op
                }

                var intent = new TradeIntent(
                    info.Symbol, info.Direction, OrderType.Market, null,
                    info.CurrentStopLoss, info.TakeProfit, "reconciled", "standard",
                    "Reconciled from venue snapshot", clock.UtcNow);
                var request = new OrderRequest(intent, info.Lots, info.Symbol, info.Direction, OrderType.Market, null);
                _pendingIntent[info.PositionId] = (request, 0m, "standard");

                var submitted = new OrderSubmitted(
                    info.PositionId, info.Symbol, info.Direction, info.Lots, null,
                    intent.StrategyId, clock.UtcNow, info.CurrentStopLoss, info.TakeProfit);
                _state = EngineReducer.Apply(_state, submitted).State;

                var filled = new OrderFilled(info.PositionId, info.Symbol, info.Lots, info.EntryPrice, clock.UtcNow);
                _state = EngineReducer.Apply(_state, filled).State;

                var ps = _state.Positions.Values.FirstOrDefault(p => p.OrderId == info.PositionId);
                if (ps is null || ps.Phase != PositionPhase.Open) { continue; }

                var position = ToPosition(ps);
                var pmOptions = strategies.FirstOrDefault(s => s.Id == position.StrategyId)?.Config.PositionManagement
                    ?? new PositionManagementOptions();
                var posConfig = PositionManager.BuildConfig(position.StrategyId, pmOptions, 0m);
                positionManager.RegisterPosition(position, posConfig);
                riskManager.RegisterPosition(position.Id, position.StrategyId, 0m);
                signalGate?.OnPositionOpened(position.StrategyId, position.Symbol.Value, position.Direction, clock.UtcNow);

                logger.LogInformation("RECONCILED|Id={Id}|Order={Order}|{Symbol}|{Dir}|lots={Lots}|entry={Entry:F5}|sl={Sl:F5}",
                    position.Id, position.OrderId, position.Symbol, position.Direction,
                    position.Lots, position.EntryPrice.Value, position.CurrentStopLoss.Value);
            }
        }
        finally { _mutex.Release(); }
    }

    /// <summary>
    /// V3 — write a venue-confirmed stop-loss/take-profit back onto the tracked position so the
    /// engine's risk/exit view (and backtest SL/TP simulation) follows the venue after a trailing
    /// modify, instead of drifting from a stale, fire-and-forget value.
    /// </summary>
    public void ConfirmStopLoss(Guid orderId, Price newStopLoss, Price? newTakeProfit)
    {
        _mutex.Wait();
        try
        {
            var ps = _state.Positions.Values.FirstOrDefault(p => p.OrderId == orderId);
            if (ps is null)
            {
                logger.LogWarning("SL_WRITEBACK_NO_POSITION|order={Order}", orderId);
                return;
            }

            var updated = ps with
            {
                CurrentStopLoss = newStopLoss,
                TakeProfit = newTakeProfit ?? ps.TakeProfit,
            };
            var newPositions = new Dictionary<Guid, PositionState>(_state.Positions)
            {
                [ps.PositionId] = updated,
            };
            _state = _state with { Positions = newPositions };

            logger.LogInformation("SL_WRITEBACK|order={Order}|sl={Sl:F5}|tp={Tp}",
                orderId, newStopLoss.Value, newTakeProfit?.Value.ToString("F5") ?? "unchanged");
        }
        finally { _mutex.Release(); }
    }

    /// <summary>
    /// Stamp the reason a position is about to close with (SL / TP / DailyDD / MaxDD / ...), WITHOUT
    /// changing its phase or emitting effects. The engine detects the exit (e.g. SimulateBarExits
    /// sees price cross the SL/TP) and records WHY here; when the venue fill lands, the lifecycle
    /// closes the position carrying this reason instead of the generic "FORCE". This is what makes
    /// the journal/trade-ledger exit reasons accurate (a TP-hit reads "TP", not "FORCE").
    /// </summary>
    public void SetCloseReason(Guid orderId, string reason)
    {
        _mutex.Wait();
        try
        {
            var ps = _state.Positions.Values.FirstOrDefault(p => p.OrderId == orderId);
            if (ps is null) return;
            var newPositions = new Dictionary<Guid, PositionState>(_state.Positions)
            {
                [ps.PositionId] = ps with { CloseReason = reason },
            };
            _state = _state with { Positions = newPositions };
        }
        finally { _mutex.Release(); }
    }

    public async Task RequestForceCloseAllAsync(string reason)
    {
        await _mutex.WaitAsync();
        try
        {
            // Stamp the reason on every open position first so the resulting close fills carry it
            // (e.g. "DailyDD"/"MaxDD") through to the ledger rather than the generic "FORCE".
            var stamped = new Dictionary<Guid, PositionState>(_state.Positions);
            foreach (var (id, ps) in _state.Positions)
                stamped[id] = ps with { CloseReason = reason };
            _state = _state with { Positions = stamped };

            var evt = new ForceCloseAllRequested(reason, clock.UtcNow);
            var decision = EngineReducer.Apply(_state, evt);
            _state = decision.State;

            if (effectExecutor is not null)
            {
                foreach (var effect in decision.Effects)
                    await effectExecutor.ExecuteAsync(effect, CancellationToken.None);
            }
        }
        finally { _mutex.Release(); }
    }

    public async Task<IReadOnlyList<EngineEffect>?> OnExecutionAsync(ExecutionEvent evt, IEnumerable<IStrategy> strategies)
    {
        await _mutex.WaitAsync();
        try
        {
            return await OnExecutionCoreAsync(evt, strategies);
        }
        finally { _mutex.Release(); }
    }

    private async Task<IReadOnlyList<EngineEffect>?> OnExecutionCoreAsync(ExecutionEvent evt, IEnumerable<IStrategy> strategies)
    {
        // F8: exact-duplicate guard. A resent identical fill on an Open position would otherwise be
        // fed to the reducer as (Open, OrderFilled) → an unintended close/reduce.
        var sig = (evt.NewState, evt.FillPrice?.Value, evt.FilledLots, evt.TimestampUtc);
        if (_lastExecSig.TryGetValue(evt.OrderId, out var prevSig) && prevSig == sig)
        {
            logger.LogWarning("Duplicate execution event skipped (identical signature). OrderId={OrderId}", evt.OrderId);
            return null;
        }
        _lastExecSig[evt.OrderId] = sig;

        if (_processedExecutionIds.Contains(evt.OrderId) && !_state.Positions.Any(kv => kv.Value.OrderId == evt.OrderId))
        {
            logger.LogWarning("Duplicate execution event skipped. OrderId={OrderId}", evt.OrderId);
            return null;
        }
        _processedExecutionIds.Add(evt.OrderId);

        // Venue-initiated close (server-side SL/TP/stop-out): stamp the venue's reason before the
        // lifecycle runs so the close fill is journaled as SL/TP, not "FORCE". The engine never
        // requested this close, so it has no reason of its own. Don't override a reason the engine
        // already set (e.g. an engine-detected exit it's also closing).
        if (evt.CloseReason is { } venueReason)
        {
            var open = _state.Positions.Values.FirstOrDefault(p => p.OrderId == evt.OrderId);
            if (open is not null && open.CloseReason is null)
            {
                var stamped = new Dictionary<Guid, PositionState>(_state.Positions)
                {
                    [open.PositionId] = open with { CloseReason = venueReason },
                };
                _state = _state with { Positions = stamped };
            }
        }

        var symbol = GetSymbolForOrder(evt.OrderId);
        var engineEvent = evt.NewState switch
        {
            OrderState.Rejected => (EngineEvent)new OrderRejected(evt.OrderId, symbol, evt.RejectionReason ?? "unknown", evt.TimestampUtc),
            // A venue cancellation (e.g. an expired resting limit) carries no fill. Route it to the
            // dedicated OrderCancelled event so the lifecycle terminates the entry instead of the old
            // `_ => OrderFilled` default, which mis-read it as a zero-lot fill (stuck Submitted, leaked
            // pending intent, journaled as "PartialFill").
            OrderState.Cancelled => new OrderCancelled(evt.OrderId, symbol, evt.RejectionReason ?? "CANCELLED", evt.TimestampUtc),
            OrderState.Filled when evt.FillPrice is not null => new OrderFilled(evt.OrderId, symbol, evt.FilledLots, evt.FillPrice ?? new Price(0), evt.TimestampUtc),
            OrderState.PartiallyFilled when evt.FillPrice is not null => new OrderPartiallyFilled(evt.OrderId, symbol, evt.FilledLots, evt.FillPrice ?? new Price(0), evt.TimestampUtc),
            _ => (EngineEvent)new OrderFilled(evt.OrderId, symbol, evt.FilledLots, evt.FillPrice ?? new Price(0), evt.TimestampUtc)
        };

        var beforePhase = FindPhase(evt.OrderId);
        var decision = EngineReducer.Apply(_state, engineEvent);
        var afterPhase = FindPhaseIn(decision.State, evt.OrderId);
        _state = decision.State;

        foreach (var effect in decision.Effects)
        {
            var execEffect = effect;
            if (execEffect is RegisterRisk reg && _pendingIntent.TryGetValue(evt.OrderId, out var pi))
            {
                execEffect = reg with { RiskAmount = pi.RiskAmount };
            }
            // Carry the venue-authoritative PnL (commission/swap-inclusive) from the execution event
            // onto the close effect so the ledger matches the account, not a cost-free recompute.
            else if (execEffect is PublishTradeClosed tc && evt.NetProfit is not null)
            {
                execEffect = tc with
                {
                    GrossProfit = evt.GrossProfit,
                    NetProfit = evt.NetProfit,
                    Commission = evt.Commission,
                    Swap = evt.Swap,
                };
            }
            // Enrich the close journal record (otherwise an empty "{}") with the itemised economics, so
            // a reader of the journal sees gross/commission/swap/net + the exit price next to the exit
            // reason. Only close fills carry NetProfit, so this never touches entry-fill records.
            else if (execEffect is RecordDecisionEvent rec && evt.NetProfit is not null)
            {
                var detail = System.Text.Json.JsonSerializer.Serialize(new
                {
                    exit = evt.FillPrice?.Value,
                    gross = evt.GrossProfit,
                    commission = evt.Commission,
                    swap = evt.Swap,
                    net = evt.NetProfit,
                });
                execEffect = rec with { Decision = rec.Decision with { DetailJson = detail } };
            }

            if (effectExecutor is not null)
                await effectExecutor.ExecuteAsync(execEffect, CancellationToken.None);
        }

        if (afterPhase == PositionPhase.Rejected)
        {
            _pendingIntent.Remove(evt.OrderId);
            logger.LogWarning("Order rejected. Id={Id} Reason={Reason}", evt.OrderId, evt.RejectionReason ?? "unknown");
            return decision.Effects;
        }

        if (afterPhase == PositionPhase.Cancelled)
        {
            _pendingIntent.Remove(evt.OrderId);
            _processedExecutionIds.Remove(evt.OrderId);
            _lastExecSig.Remove(evt.OrderId);
            logger.LogInformation("Order cancelled. Id={Id} Reason={Reason}", evt.OrderId, evt.RejectionReason ?? "CANCELLED");
            return decision.Effects;
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
            return decision.Effects;
        }

        var fillPrice = evt.FillPrice?.Value ?? 0;

        if (afterPhase == PositionPhase.Reducing)
        {
            await HandlePartialCloseAsync(evt, fillPrice, strategies);
            return decision.Effects;
        }

        if (afterPhase == PositionPhase.Closed)
        {
            await ClosePositionAsync(evt, fillPrice, strategies);
            return decision.Effects;
        }

        return decision.Effects;
    }

    private void OnOpened(ExecutionEvent evt, IEnumerable<IStrategy> strategies)
    {
        var ps = _state.Positions.Values.FirstOrDefault(p => p.OrderId == evt.OrderId);
        if (ps is null) return;

        var (request, riskAmount, riskProfileId) = _pendingIntent.GetValueOrDefault(evt.OrderId,
            (default!, 0m, "standard"));

        // F6 (iter-26): register under the SAME id used to deregister (the reducer's
        // DeregisterRisk/DeregisterPosition carry ps.PositionId). Using a fresh Guid here meant
        // PositionManager.DeregisterPosition never matched → its dictionaries leaked every trade.
        var position = new Position(
            ps.PositionId, evt.OrderId, ps.Symbol, ps.Direction,
            ps.Lots, ps.EntryPrice, ps.CurrentStopLoss, ps.TakeProfit,
            clock.UtcNow, ps.StrategyId);

        var pmOptions = strategies.FirstOrDefault(s => s.Id == position.StrategyId)?.Config.PositionManagement
            ?? new PositionManagementOptions();
        var posConfig = PositionManager.BuildConfig(position.StrategyId, pmOptions, riskAmount);
        positionManager.RegisterPosition(position, posConfig);

        signalGate?.OnPositionOpened(position.StrategyId, position.Symbol.Value, position.Direction, clock.UtcNow);

        logger.LogInformation("Opened. Id={Id} Symbol={Symbol} Dir={Dir} Lots={Lots} Entry={Entry:F5}",
            position.Id, position.Symbol, position.Direction, position.Lots, position.EntryPrice.Value);
    }

    private async Task ClosePositionAsync(ExecutionEvent evt, decimal fillPrice, IEnumerable<IStrategy> strategies)
    {
        var ps = _state.Positions.Values.FirstOrDefault(p => p.OrderId == evt.OrderId);
        if (ps is null) return;

        _processedExecutionIds.Remove(evt.OrderId);
        _lastExecSig.Remove(evt.OrderId);

        logger.LogInformation("Closed. Id={Id} Exit={Exit:F5} Reason={Reason}", ps.PositionId, fillPrice, ps.CloseReason ?? "FORCE");
    }

    private async Task HandlePartialCloseAsync(ExecutionEvent evt, decimal fillPrice, IEnumerable<IStrategy> strategies)
    {
        var ps = _state.Positions.Values.FirstOrDefault(p => p.OrderId == evt.OrderId);
        if (ps is null) return;

        var position = ToPosition(ps);
        var closedLots = evt.FilledLots;
        var remainingLots = ps.Lots; // reducer already shrank ps.Lots to the remainder

        var symbolInfo = symbolRegistry.Get(position.Symbol);
        var pnl = PipCalculator.GrossPnL(position.Direction, position.EntryPrice, new Price(fillPrice), closedLots, symbolInfo, crossRateProvider);

        var (request, riskAmount, riskProfileId) = _pendingIntent.GetValueOrDefault(evt.OrderId, (default!, 0m, "standard"));
        // F4 (iter-26): scale the registered risk by remaining/ORIGINAL lots. Previously this divided
        // by position.Lots (== remainingLots after the reduce), so the ratio was always 1 and open
        // risk never dropped after a partial close — over-blocking later entries.
        var originalLots = request is not null ? request.Lots : remainingLots + closedLots;
        var proportionalRisk = originalLots > 0 ? riskAmount * remainingLots / originalLots : 0m;
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
}
