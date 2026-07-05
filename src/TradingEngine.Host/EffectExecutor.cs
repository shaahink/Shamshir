using Microsoft.Extensions.Logging;
using TradingEngine.Domain.Events;
using TradingEngine.Services.Helpers;

namespace TradingEngine.Host;

public sealed class EffectExecutor : IEffectExecutor
{
    private readonly IBrokerAdapter _broker;
    private readonly IEventBus _eventBus;
    private readonly IDecisionJournal? _decisionJournal;
    private readonly IEquitySink? _equitySink;
    private readonly IProgress<BacktestProgressEvent>? _progress;
    private readonly string _runId;
    private readonly IEngineClock _clock;
    private readonly ILogger<EffectExecutor> _logger;
    private readonly ISymbolInfoRegistry _symbolRegistry;
    private readonly Func<string, string, decimal> _crossRateProvider;
    private readonly IReadOnlyList<IStrategy> _strategies;
    private readonly ITradingGovernor? _governor;
    private readonly ISignalGate? _signalGate;
    private readonly IRiskManager _riskManager;
    private readonly IPositionManager _positionManager;

    public EffectExecutor(
        IBrokerAdapter broker,
        IEventBus eventBus,
        IDecisionJournal decisionJournal,
        IEquitySink? equitySink,
        IProgress<BacktestProgressEvent>? progress,
        EngineRunContext runContext,
        IEngineClock clock,
        ILogger<EffectExecutor> logger,
        ISymbolInfoRegistry symbolRegistry,
        Func<string, string, decimal> crossRateProvider,
        IReadOnlyList<IStrategy> strategies,
        IRiskManager riskManager,
        IPositionManager positionManager,
        ITradingGovernor? governor = null,
        ISignalGate? signalGate = null)
    {
        _broker = broker;
        _eventBus = eventBus;
        _decisionJournal = decisionJournal;
        _equitySink = equitySink;
        _progress = progress;
        _runId = runContext.RunId;
        _clock = clock;
        _logger = logger;
        _symbolRegistry = symbolRegistry;
        _crossRateProvider = crossRateProvider;
        _strategies = strategies;
        _governor = governor;
        _signalGate = signalGate;
        _riskManager = riskManager;
        _positionManager = positionManager;
    }

    public async Task ExecuteAsync(EngineEffect effect, CancellationToken ct)
    {
        switch (effect)
        {
            case SubmitOrder submit:
                // P2.7: OrderType now travels on the effect itself (set by the kernel from the proposal),
                // not re-derived from LimitPrice presence — that derivation couldn't distinguish Stop from
                // Limit (both carry a resting trigger price on LimitPrice).
                var intent = new TradeIntent(submit.Symbol, submit.Direction, submit.OrderType,
                    submit.LimitPrice, submit.StopLoss, submit.TakeProfit,
                    submit.StrategyId, "standard", "", _clock.UtcNow);
                var orderReq = new OrderRequest(intent, submit.Lots, submit.Symbol, submit.Direction,
                    submit.OrderType, submit.LimitPrice,
                    // Submit under the kernel's order id (= PositionId) so the venue fill/close + the
                    // feedback bridge all key off ONE id — no venue-id↔kernel-id translation (K2).
                    ClientOrderId: submit.OrderId);
                await _broker.SubmitOrderAsync(orderReq, ct);
                break;

            case ModifyStopLoss modSl:
                await _broker.ModifyOrderAsync(modSl.PositionId, modSl.NewStopLoss, modSl.TakeProfit, ct);
                break;

            case ModifyTakeProfit modTp:
                await _broker.ModifyOrderAsync(modTp.PositionId, new Price(0), modTp.NewTakeProfit, ct);
                break;

            case CloseOpenPosition closePos:
                // A close REQUEST — not a completed close. The funnel "Closes" counter is incremented
                // on PublishTradeClosed (the actual fill), so we don't emit a "CLOSE" progress here
                // (doing so double-counted force-closes, which emit both this and PublishTradeClosed).
                // An engine-detected SL/TP carries the stop/target price so the close fills there (K2);
                // a force-close (no price) routes to the normal market close.
                if (closePos.ExitPrice is { } exitPx)
                    await _broker.ClosePositionAtAsync(closePos.OrderId, exitPx, ct);
                else
                    await _broker.ClosePositionAsync(closePos.OrderId, ct);
                break;

            case ClosePartialOpenPosition partial:
                // iter-38 A4b: close part of the position; the venue emits a partial fill that reduces it.
                await _broker.ClosePartialPositionAsync(partial.OrderId, partial.CloseLots, ct);
                break;

            case RecordDecisionEvent record:
                // iter-36 K5: the gate decision is now journaled losslessly on the StepRecord (DecisionReason),
                // so the kernel path no longer needs the old IDecisionJournal. Kept optional only for the
                // golden oracle harness, which still asserts on it; production passes null / a no-op.
                _decisionJournal?.Record(record.Decision);
                break;

            case PublishTradeClosed tradeClosed:
                await HandlePublishTradeClosed(tradeClosed, ct);
                break;

            case RegisterRisk register:
                _riskManager.RegisterPosition(register.PositionId, register.StrategyId, register.RiskAmount);
                break;

            case DeregisterRisk deregister:
                _riskManager.DeregisterPosition(deregister.PositionId);
                _positionManager.DeregisterPosition(deregister.PositionId);
                break;
        }
    }

    private async Task HandlePublishTradeClosed(PublishTradeClosed effect, CancellationToken ct)
    {
        var symbolInfo = _symbolRegistry.Get(effect.Symbol);
        var recomputedGross = PipCalculator.GrossPnL(effect.Direction, effect.EntryPrice, effect.ExitPrice,
            effect.Lots, symbolInfo, _crossRateProvider);

        // Prefer the venue-authoritative PnL (commission/swap-inclusive) when the live venue reported
        // it; only fall back to the price-recomputed gross for the simulated venue.
        var currency = recomputedGross.Currency;
        var gross = effect.GrossProfit is { } g ? new Money(g, currency) : recomputedGross;
        var commission = new Money(effect.Commission ?? 0m, currency);
        var swap = new Money(effect.Swap ?? 0m, currency);
        var net = effect.NetProfit is { } n ? new Money(n, currency) : gross.Subtract(commission).Subtract(swap);

        // Trade analytics, previously hardcoded to zero. Derived from the close geometry so they are
        // always consistent with the prices shown next to them. R uses a pip-distance ratio (reward
        // over initial stop distance), so the pip size cancels and no pip-value/cross-rate is needed.
        var pipSize = symbolInfo.PipSize;
        var entry = effect.EntryPrice.Value;
        var exit = effect.ExitPrice.Value;
        var isLong = effect.Direction == TradeDirection.Long;
        var signedMove = isLong ? exit - entry : entry - exit;

        var pnlPips = new Pips((double)(signedMove / pipSize));

        // P0.1: R against the stop taken AT ENTRY (InitialStopLoss), never effect.StopLoss (the
        // current/final stop — breakeven/trailing may have moved it to near-zero risk by close time).
        var rMultiple = PipCalculator.RMultiple(effect.Direction, effect.EntryPrice, effect.ExitPrice, effect.InitialStopLoss);

        // Most-favorable / most-adverse prices over the position's life. HighWater/LowWater are the
        // per-bar extremes carried on the effect; fold in entry & exit so a same-bar close still yields
        // a sane (>= 0) magnitude, and ignore unset (zero) water marks.
        var hi = Math.Max(entry, exit);
        if (effect.HighWater > 0) hi = Math.Max(hi, effect.HighWater);
        var lo = Math.Min(entry, exit);
        if (effect.LowWater > 0) lo = Math.Min(lo, effect.LowWater);

        var mfePips = new Pips((double)((isLong ? hi - entry : entry - lo) / pipSize));
        var maePips = new Pips((double)((isLong ? entry - lo : hi - entry) / pipSize));

        var tradeResult = new TradeResult(Guid.NewGuid(), effect.PositionId, effect.Symbol, effect.Direction,
            effect.Lots, effect.EntryPrice, effect.ExitPrice, effect.StopLoss, effect.TakeProfit,
            effect.OpenedAtUtc, effect.ClosedAtUtc, gross, commission, swap,
            net, pnlPips, rMultiple, maePips, mfePips,
            effect.ExitReason, effect.StrategyId, effect.RiskProfileId ?? "standard",
            OrderEntryMethod: effect.OrderEntryMethod,
            OrderId: effect.OrderId,
            EntryReason: effect.EntryReason,
            EntryRegime: effect.EntryRegime,
            InitialStopLoss: effect.InitialStopLoss,
            EntrySnapshotJson: System.Text.Json.JsonSerializer.Serialize(new
            {
                reason = effect.EntryReason,
                regime = effect.EntryRegime,
                direction = effect.Direction.ToString(),
                entryPrice = effect.EntryPrice.Value,
                // P0.1: this used to be effect.StopLoss (the current/final stop AT CLOSE, which
                // breakeven/trailing may have moved) despite the "entry snapshot" name. Now the actual
                // stop the trade risked at entry, matching the name.
                stopLoss = effect.InitialStopLoss.Value,
                takeProfit = effect.TakeProfit?.Value,
                lots = effect.Lots
            }));

        foreach (var s in _strategies.Where(s => s.Id == effect.StrategyId))
        {
            s.OnTradeResult(tradeResult);
        }

        await _eventBus.PublishAsync(new TradeClosed(tradeResult, _runId, effect.ClosedAtUtc), ct);

        _governor?.OnTradeClosed(tradeResult);
        _signalGate?.OnPositionClosed(effect.StrategyId, effect.Symbol.Value, effect.Direction,
            effect.ExitReason, effect.ClosedAtUtc);

        // Live funnel "Closes" + journal: emitted once per actually-closed trade.
        _progress?.Report(new BacktestProgressEvent(_runId, "CLOSE",
            $"{effect.Symbol.Value} {effect.Direction} exit={effect.ExitPrice.Value:F5} net={net.Amount:F2} reason={effect.ExitReason}",
            effect.ClosedAtUtc));

        _logger.LogInformation("CLOSED|{Symbol}|{Dir}|Exit={Exit:F5}|Gross={Gross:F2}|Net={Net:F2}|Reason={Reason}",
            effect.Symbol.Value, effect.Direction, effect.ExitPrice.Value, gross.Amount, net.Amount, effect.ExitReason);
    }
}
