namespace TradingEngine.Services.Strategy.Behaviors;

public sealed class AtrTrailingBehavior(string atrKey, double multiplier) : IPositionBehavior
{
    private readonly Dictionary<Guid, decimal> _highWater = [];
    private readonly Dictionary<Guid, decimal> _lowWater = [];

    public string BehaviorId => "atr-trail";

    public IReadOnlyList<PositionModification> Evaluate(
        Position pos, Tick tick, IReadOnlyDictionary<string, double> indicators, SymbolInfo sym)
    {
        if (pos.Direction == TradeDirection.Long)
            _highWater[pos.Id] = Math.Max(_highWater.GetValueOrDefault(pos.Id, pos.EntryPrice.Value), tick.Bid);
        else
            _lowWater[pos.Id] = Math.Min(_lowWater.GetValueOrDefault(pos.Id, pos.EntryPrice.Value), tick.Ask);

        var atr = indicators.GetValueOrDefault(atrKey);
        var hw = _highWater.GetValueOrDefault(pos.Id, pos.EntryPrice.Value);
        var lw = _lowWater.GetValueOrDefault(pos.Id, pos.EntryPrice.Value);
        var newSl = TrailingHelpers.AtrTrail(pos, hw, lw, atr, multiplier, sym);
        return newSl.HasValue ? [new MoveStopLoss(pos.Id, newSl.Value)] : [];
    }
}
