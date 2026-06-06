namespace TradingEngine.Services;

public sealed class PositionManager(ISymbolInfoRegistry symbolRegistry) : IPositionManager
{
    private readonly Dictionary<Guid, (Position Pos, PositionManagementConfig Config)> _tracked = new();

    public void RegisterPosition(Position position, PositionManagementConfig config)
        => _tracked[position.Id] = (position, config);

    public void DeregisterPosition(Guid positionId)
        => _tracked.Remove(positionId);

    public IReadOnlyList<PositionModification> Evaluate(
        Position position, Tick currentTick, IReadOnlyList<Bar> recentBars)
    {
        if (!_tracked.TryGetValue(position.Id, out var entry)) return [];
        var (_, config) = entry;
        var mods = new List<PositionModification>();
        var symbolInfo = symbolRegistry.Get(position.Symbol);

        if (config.UseBreakeven)
        {
            var newBeSl = TrailingHelpers.Breakeven(
                position, currentTick.Bid, currentTick.Ask,
                config.BreakevenTriggerR, config.BreakevenBufferPips, symbolInfo);
            if (newBeSl.HasValue)
                mods.Add(new MoveStopLoss(position.Id, newBeSl.Value));
        }

        if (config.TrailingStop.Method != TrailingMethod.BreakevenThenTrail)
        {
            var trailingSl = TrailingHelpers.StepTrail(
                position, currentTick.Bid, currentTick.Ask,
                new Pips(config.TrailingStop.StepPips), symbolInfo);
            if (trailingSl.HasValue)
                mods.Add(new MoveStopLoss(position.Id, trailingSl.Value));
        }

        return mods;
    }
}
