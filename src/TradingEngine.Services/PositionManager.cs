using Microsoft.Extensions.Logging;

namespace TradingEngine.Services;

public sealed class PositionManager(
    ISymbolInfoRegistry symbolRegistry,
    IIndicatorService indicatorService,
    ILogger<PositionManager> logger) : IPositionManager
{
    private readonly Dictionary<Guid, (Position Pos, PositionManagementConfig Config, PositionLifecycleState State)> _tracked = new();
    private readonly HashSet<Guid> _beApplied = new();
    private readonly Dictionary<Guid, decimal> _highWaterBid = new();
    private readonly Dictionary<Guid, decimal> _lowWaterAsk = new();
    private readonly Dictionary<Guid, decimal> _initialSlDistance = new();

    public void RegisterPosition(Position position, PositionManagementConfig config)
    {
        _tracked[position.Id] = (position, config, PositionLifecycleState.Active);
        _highWaterBid[position.Id] = position.EntryPrice.Value;
        _lowWaterAsk[position.Id] = position.EntryPrice.Value;
        logger.LogInformation("Position state changed. Id={Id} From=None To=Active", position.Id);
    }

    public void DeregisterPosition(Guid positionId)
    {
        if (_tracked.TryGetValue(positionId, out var entry))
        {
            var prevState = entry.State;
            logger.LogInformation("Position state changed. Id={Id} From={From} To=Closed", positionId, prevState);
        }
        _tracked.Remove(positionId);
        _beApplied.Remove(positionId);
        _highWaterBid.Remove(positionId);
        _lowWaterAsk.Remove(positionId);
        _initialSlDistance.Remove(positionId);
    }

    public IReadOnlyList<PositionModification> Evaluate(
        Position position, Tick currentTick, IReadOnlyList<Bar> recentBars)
    {
        if (!_tracked.TryGetValue(position.Id, out var entry)) return [];
        var (_, config, state) = entry;
        if (state == PositionLifecycleState.Closed) return [];

        var mods = new List<PositionModification>();
        var symbolInfo = symbolRegistry.Get(position.Symbol);

        _highWaterBid[position.Id] = Math.Max(
            _highWaterBid.GetValueOrDefault(position.Id, position.EntryPrice.Value), currentTick.Bid);
        _lowWaterAsk[position.Id] = Math.Min(
            _lowWaterAsk.GetValueOrDefault(position.Id, position.EntryPrice.Value), currentTick.Ask);

        var newState = state;

        if (config.UseBreakeven && !_beApplied.Contains(position.Id))
        {
            var newBeSl = TrailingHelpers.Breakeven(
                position, currentTick.Bid, currentTick.Ask,
                config.BreakevenTriggerR, config.BreakevenBufferPips, symbolInfo);
            if (newBeSl.HasValue)
            {
                _beApplied.Add(position.Id);
                mods.Add(new MoveStopLoss(position.Id, newBeSl.Value));
                newState = PositionLifecycleState.BreakevenSet;
                logger.LogDebug("Breakeven applied. Id={Id} NewSl={Sl:F5}", position.Id, newBeSl.Value.Value);
            }
        }

        if (config.TrailingStop.Method == TrailingMethod.StepPips && !_beApplied.Contains(position.Id) && state != PositionLifecycleState.BreakevenSet)
        {
            var trailingSl = TrailingHelpers.StepTrail(
                position, currentTick.Bid, currentTick.Ask,
                new Pips(config.TrailingStop.StepPips), symbolInfo);
            if (trailingSl.HasValue)
            {
                mods.Add(new MoveStopLoss(position.Id, trailingSl.Value));
                newState = PositionLifecycleState.Trailing;
            }
        }

        if (config.TrailingStop.Method == TrailingMethod.AtrMultiple && !_beApplied.Contains(position.Id))
        {
            var atrValue = ComputeAtr(recentBars, config);
            var atrMultiple = GetEffectiveAtrMultiple(config);

            var atrTrail = TrailingHelpers.AtrTrail(
                position,
                _highWaterBid.GetValueOrDefault(position.Id, position.EntryPrice.Value),
                _lowWaterAsk.GetValueOrDefault(position.Id, position.EntryPrice.Value),
                atrValue, atrMultiple, symbolInfo);
            if (atrTrail.HasValue)
            {
                mods.Add(new MoveStopLoss(position.Id, atrTrail.Value));
                newState = PositionLifecycleState.Trailing;
            }
        }

        if (config.TrailingStop.Method == TrailingMethod.Structure && !_beApplied.Contains(position.Id))
        {
            var lookback = config.TrailingStop.StructureLookbackBars > 0
                ? config.TrailingStop.StructureLookbackBars
                : 10;
            var atrValue = ComputeAtr(recentBars, config);
            var atrMultiple = GetEffectiveAtrMultiple(config);

            var structureSl = TrailingHelpers.StructureTrail(
                position, recentBars, lookback, atrValue, atrMultiple, symbolInfo);
            if (structureSl.HasValue)
            {
                mods.Add(new MoveStopLoss(position.Id, structureSl.Value));
                newState = PositionLifecycleState.Trailing;
            }
        }

        if (config.TrailingStop.Method == TrailingMethod.SteppedR && !_beApplied.Contains(position.Id))
        {
            var steppedRLevels = config.TrailingStop.SteppedRLevels is { Length: > 0 }
                ? config.TrailingStop.SteppedRLevels
                : new[] { 1.0, 2.0, 3.0 };

            var initialSl = _initialSlDistance.GetValueOrDefault(position.Id);
            if (initialSl <= 0)
            {
                initialSl = Math.Abs(position.EntryPrice.Value - position.CurrentStopLoss.Value);
                _initialSlDistance[position.Id] = initialSl;
            }

            var steppedSl = TrailingHelpers.SteppedRTrail(
                position, currentTick.Bid, currentTick.Ask, steppedRLevels, initialSl, symbolInfo);
            if (steppedSl.HasValue)
            {
                mods.Add(new MoveStopLoss(position.Id, steppedSl.Value));
                newState = PositionLifecycleState.Trailing;
            }
        }

        if (config.TrailingStop.Method == TrailingMethod.BreakevenThenTrail && !_beApplied.Contains(position.Id))
        {
            var beTrail = TrailingHelpers.Breakeven(
                position, currentTick.Bid, currentTick.Ask,
                config.TrailingStop.BreakevenTriggerR, new Pips(1), symbolInfo);
            if (beTrail.HasValue)
            {
                _beApplied.Add(position.Id);
                mods.Add(new MoveStopLoss(position.Id, beTrail.Value));
                newState = PositionLifecycleState.BreakevenSet;
            }
        }

        if (newState != state)
        {
            _tracked[position.Id] = (position, config, newState);
            logger.LogInformation("Position state changed. Id={Id} From={From} To={To}", position.Id, state, newState);
        }

        return mods;
    }

    private double ComputeAtr(IReadOnlyList<Bar> recentBars, PositionManagementConfig config)
    {
        if (recentBars.Count == 0) return config.TrailingStop.AtrMultiple * 0.0001;
        var atrValue = indicatorService.Atr(recentBars, Math.Min(14, recentBars.Count));
        if (atrValue <= 0) atrValue = config.TrailingStop.AtrMultiple * 0.0001;
        return atrValue;
    }

    private double GetEffectiveAtrMultiple(PositionManagementConfig config)
    {
        if (!config.TrailingStop.RideEnabled) return config.TrailingStop.AtrMultiple;

        return config.TrailingStop.AtrMultiple > 0
            ? config.TrailingStop.RideRelaxedAtrMultiple
            : config.TrailingStop.AtrMultiple;
    }
}
