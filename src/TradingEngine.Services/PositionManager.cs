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
            var atrValue = 0.0;
            if (recentBars.Count > 0)
            {
                atrValue = indicatorService.Atr(recentBars, Math.Max(14, recentBars.Count - 1));
                if (atrValue <= 0) atrValue = config.TrailingStop.AtrMultiple * (double)symbolInfo.PipSize;
            }

            var atrTrail = TrailingHelpers.AtrTrail(
                position,
                _highWaterBid.GetValueOrDefault(position.Id, position.EntryPrice.Value),
                _lowWaterAsk.GetValueOrDefault(position.Id, position.EntryPrice.Value),
                (double)atrValue, config.TrailingStop.AtrMultiple, symbolInfo);
            if (atrTrail.HasValue)
            {
                mods.Add(new MoveStopLoss(position.Id, atrTrail.Value));
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
}
