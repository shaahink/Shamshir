using Microsoft.Extensions.Logging;
using TradingEngine.Engine;

namespace TradingEngine.Services;

public sealed class PositionManager(
    ISymbolInfoRegistry symbolRegistry,
    IIndicatorService indicatorService,
    ILogger<PositionManager> logger) : IPositionManager
{
    private const string StateActive = "Active";
    private const string StateBreakevenSet = "BreakevenSet";
    private const string StateTrailing = "Trailing";
    private const string StateClosed = "Closed";

    private readonly Dictionary<Guid, (Position Pos, PositionManagementConfig Config, string State)> _tracked = new();
    private readonly HashSet<Guid> _beApplied = new();
    private readonly Dictionary<Guid, decimal> _highWaterBid = new();
    private readonly Dictionary<Guid, decimal> _lowWaterAsk = new();
    private readonly Dictionary<Guid, decimal> _initialSlDistance = new();

    public void RegisterPosition(Position position, PositionManagementConfig config)
    {
        _tracked[position.Id] = (position, config, StateActive);
        _highWaterBid[position.Id] = position.EntryPrice.Value;
        _lowWaterAsk[position.Id] = position.EntryPrice.Value;
        logger.LogInformation("Position state changed. Id={Id} From=None To=Active", position.Id);
    }

    public void DeregisterPosition(Guid positionId)
    {
        if (_tracked.TryGetValue(positionId, out var entry))
        {
            logger.LogInformation("Position state changed. Id={Id} From={From} To=Closed", positionId, entry.State);
        }
        _tracked.Remove(positionId);
        _beApplied.Remove(positionId);
        _highWaterBid.Remove(positionId);
        _lowWaterAsk.Remove(positionId);
        _initialSlDistance.Remove(positionId);
    }

    /// <summary>
    /// Per-bar position management. Breakeven and trailing now COEXIST: each computes a candidate stop
    /// and the single MOST-FAVOURABLE one (highest for long / lowest for short) that beats the current
    /// stop is emitted. The old code guarded every trailing branch with <c>!_beApplied</c>, so enabling
    /// breakeven permanently DISABLED trailing ("BE then trail" never trailed); and it could emit two
    /// mods for the same bar, which — applied in arbitrary order via a non-monotonic write-back — could
    /// move the stop backwards. The underlying <see cref="PositionLifecycle"/> helpers are monotonic, so
    /// taking the most-favourable candidate is correct.
    /// </summary>
    public IReadOnlyList<PositionModification> Evaluate(
        Position position, Tick currentTick, IReadOnlyList<Bar> recentBars)
    {
        if (!_tracked.TryGetValue(position.Id, out var entry)) return [];
        var (_, config, state) = entry;
        if (state == StateClosed) return [];

        var symbolInfo = symbolRegistry.Get(position.Symbol);
        var ps = ToPositionState(position);

        _highWaterBid[position.Id] = Math.Max(
            _highWaterBid.GetValueOrDefault(position.Id, position.EntryPrice.Value), currentTick.Bid);
        _lowWaterAsk[position.Id] = Math.Min(
            _lowWaterAsk.GetValueOrDefault(position.Id, position.EntryPrice.Value), currentTick.Ask);

        Price? candidate = null;
        var breakevenTriggered = false;

        // Breakeven floor (once).
        if (config.UseBreakeven && !_beApplied.Contains(position.Id))
        {
            var beSl = PositionLifecycle.TryBreakeven(
                ps, currentTick.Bid, currentTick.Ask,
                config.BreakevenTriggerR, config.BreakevenBufferPips, symbolInfo);
            if (beSl.HasValue)
            {
                candidate = MoreFavorable(candidate, beSl.Value, position.Direction);
                breakevenTriggered = true;
            }
        }

        // Trailing (every bar; independent of breakeven — the monotonic guard ratchets the stop up).
        var trail = ComputeTrail(config, ps, currentTick, recentBars, symbolInfo);
        if (trail.HasValue)
            candidate = MoreFavorable(candidate, trail.Value, position.Direction);

        if (candidate is null || !Improves(candidate.Value, position.CurrentStopLoss, position.Direction))
            return [];

        if (breakevenTriggered) _beApplied.Add(position.Id);
        var newState = state == StateActive ? (breakevenTriggered ? StateBreakevenSet : StateTrailing) : state;
        if (newState != state)
        {
            _tracked[position.Id] = (position, config, newState);
            logger.LogInformation("Position state changed. Id={Id} From={From} To={To}", position.Id, state, newState);
        }

        return [new MoveStopLoss(position.Id, candidate.Value)];
    }

    private Price? ComputeTrail(
        PositionManagementConfig config, PositionState ps, Tick tick,
        IReadOnlyList<Bar> recentBars, SymbolInfo symbolInfo)
    {
        var t = config.TrailingStop;
        switch (t.Method)
        {
            case TrailingMethod.StepPips:
                return PositionLifecycle.TrailStepPips(ps, tick.Bid, tick.Ask, new Pips(t.StepPips), symbolInfo);

            case TrailingMethod.AtrMultiple:
                return PositionLifecycle.TrailAtr(ps,
                    _highWaterBid.GetValueOrDefault(ps.PositionId, ps.EntryPrice.Value),
                    _lowWaterAsk.GetValueOrDefault(ps.PositionId, ps.EntryPrice.Value),
                    ComputeAtr(recentBars, config, symbolInfo), EffectiveAtrMultiple(t, recentBars), symbolInfo);

            case TrailingMethod.Structure:
                return PositionLifecycle.TrailStructure(ps, recentBars,
                    t.StructureLookbackBars > 0 ? t.StructureLookbackBars : 10,
                    ComputeAtr(recentBars, config, symbolInfo), EffectiveAtrMultiple(t, recentBars), symbolInfo);

            case TrailingMethod.SteppedR:
            {
                var levels = t.SteppedRLevels is { Length: > 0 } ? t.SteppedRLevels : [1.0, 2.0, 3.0];
                var initialSl = _initialSlDistance.GetValueOrDefault(ps.PositionId);
                if (initialSl <= 0)
                {
                    initialSl = Math.Abs(ps.EntryPrice.Value - ps.CurrentStopLoss.Value);
                    _initialSlDistance[ps.PositionId] = initialSl;
                }
                return PositionLifecycle.TrailSteppedR(
                    ps with { InitialSlDistance = initialSl }, tick.Bid, tick.Ask, levels, symbolInfo);
            }

            // BreakevenThenTrail is a breakeven-trigger method (kept for back-compat). For genuine
            // "breakeven then trail", set UseBreakeven=true AND Trailing.Method=AtrMultiple/Structure.
            case TrailingMethod.BreakevenThenTrail:
                return PositionLifecycle.TryBreakeven(ps, tick.Bid, tick.Ask,
                    config.TrailingStop.BreakevenTriggerR, new Pips(1), symbolInfo);

            default: // TrailingMethod.None
                return null;
        }
    }

    private static Price MoreFavorable(Price? current, Price candidate, TradeDirection dir)
    {
        if (current is null) return candidate;
        var keepCandidate = dir == TradeDirection.Long
            ? candidate.Value > current.Value.Value
            : candidate.Value < current.Value.Value;
        return keepCandidate ? candidate : current.Value;
    }

    private static bool Improves(Price candidate, Price currentStop, TradeDirection dir)
        => dir == TradeDirection.Long
            ? candidate.Value > currentStop.Value
            : candidate.Value < currentStop.Value;

    /// <summary>
    /// Maps a strategy's declarative <see cref="PositionManagementOptions"/> (from its JSON) onto the
    /// runtime <see cref="PositionManagementConfig"/> the manager consumes. Previously the registration
    /// sites hard-coded an ATR-trail + BE@1R for every position and ignored the per-strategy JSON.
    /// </summary>
    public static PositionManagementConfig BuildConfig(
        string strategyId, PositionManagementOptions opts, decimal initialRiskAmount)
    {
        var tr = opts.Trailing;
        var trailing = new TrailingConfig(
            ParseTrailingMethod(tr.Method), tr.StepPips, tr.AtrMultiple, opts.Breakeven.TriggerRMultiple)
        {
            StructureLookbackBars = tr.StructureLookbackBars,
            SteppedRLevels = tr.SteppedRLevels,
            // iter-38 A5: Ride relaxes the ATR trail while ADX is strong. opts.Ride is already add-on-resolved
            // (Auto ⇒ tuner numbers) by the time BuildConfig runs at registration.
            RideEnabled = opts.Ride?.Enabled ?? false,
            RideAdxFloor = opts.Ride?.AdxFloor ?? 25,
            RideRelaxedAtrMultiple = opts.Ride?.RelaxedAtrMultiple ?? tr.AtrMultiple,
        };
        return new PositionManagementConfig(
            strategyId, trailing,
            opts.Breakeven.Enabled, opts.Breakeven.TriggerRMultiple, new Pips(opts.Breakeven.OffsetPips),
            new Money(initialRiskAmount, "USD"));
    }

    private static TrailingMethod ParseTrailingMethod(string method) => method switch
    {
        "StepPips" => TrailingMethod.StepPips,
        "AtrMultiple" => TrailingMethod.AtrMultiple,
        "Structure" => TrailingMethod.Structure,
        "SteppedR" => TrailingMethod.SteppedR,
        "BreakevenThenTrail" => TrailingMethod.BreakevenThenTrail,
        _ => TrailingMethod.None,
    };

    private static PositionState ToPositionState(Position p)
    {
        return new PositionState(
            p.Id, p.OrderId, p.Symbol, p.Direction, p.Lots,
            p.EntryPrice, p.CurrentStopLoss, p.TakeProfit,
            p.OpenedAtUtc, p.StrategyId, PositionPhase.Open, p.Lots);
    }

    /// <summary>iter-38 A5 (Ride): while the add-on is enabled and ADX shows a strong trend, widen the ATR
    /// trailing multiple to the relaxed value so a runner is given more room; otherwise the configured
    /// multiple stands. Off (RideEnabled=false) ⇒ always the configured multiple ⇒ golden byte-identical.</summary>
    private double EffectiveAtrMultiple(TrailingConfig t, IReadOnlyList<Bar> recentBars)
    {
        if (!t.RideEnabled || recentBars.Count < 2) return t.AtrMultiple;
        var adx = indicatorService.Adx(recentBars, Math.Min(14, recentBars.Count));
        return adx > t.RideAdxFloor ? t.RideRelaxedAtrMultiple : t.AtrMultiple;
    }

    private double ComputeAtr(IReadOnlyList<Bar> recentBars, PositionManagementConfig config, SymbolInfo symbolInfo)    {
        var fallback = (double)symbolInfo.PipSize;
        if (recentBars.Count == 0) return config.TrailingStop.AtrMultiple * fallback;
        var atrValue = indicatorService.Atr(recentBars, Math.Min(14, recentBars.Count));
        if (atrValue <= 0) atrValue = config.TrailingStop.AtrMultiple * fallback;
        return atrValue;
    }
}
