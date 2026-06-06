namespace TradingEngine.Domain;

public interface ISymbolInfoRegistry
{
    SymbolInfo Get(Symbol symbol);
    void Register(SymbolInfo info);
    bool TryGet(Symbol symbol, out SymbolInfo info);
}
