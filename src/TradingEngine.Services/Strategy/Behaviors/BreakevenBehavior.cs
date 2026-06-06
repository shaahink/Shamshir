namespace TradingEngine.Services.Strategy.Behaviors;

public sealed class BreakevenBehavior(double triggerR, Pips buffer) : IPositionBehavior
{
    private readonly HashSet<Guid> _applied = [];

    public string BehaviorId => "breakeven";

    public IReadOnlyList<PositionModification> Evaluate(
        Position pos, Tick tick, IReadOnlyDictionary<string, double> indicators, SymbolInfo sym)
    {
        if (_applied.Contains(pos.Id)) return [];
        var newSl = TrailingHelpers.Breakeven(pos, tick.Bid, tick.Ask, triggerR, buffer, sym);
        if (newSl is null) return [];
        _applied.Add(pos.Id);
        return [new MoveStopLoss(pos.Id, newSl.Value)];
    }
}
