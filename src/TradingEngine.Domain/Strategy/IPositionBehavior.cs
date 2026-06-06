namespace TradingEngine.Domain;

public interface IPositionBehavior
{
    string BehaviorId { get; }
    IReadOnlyList<PositionModification> Evaluate(
        Position pos, Tick tick, IReadOnlyDictionary<string, double> indicators, SymbolInfo sym);
}
