using System.Collections.Concurrent;

namespace TradingEngine.Infrastructure;

public sealed class SymbolInfoRegistry : ISymbolInfoRegistry
{
    private readonly ConcurrentDictionary<Symbol, SymbolInfo> _symbols = new();

    public SymbolInfo Get(Symbol symbol)
    {
        if (_symbols.TryGetValue(symbol, out var info))
            return info;
        throw new KeyNotFoundException($"Symbol {symbol} is not registered. Ensure config/symbols/defaults.json is loaded.");
    }

    public void Register(SymbolInfo info)
    {
        _symbols[info.Symbol] = info;
    }

    public bool TryGet(Symbol symbol, out SymbolInfo info)
    {
        return _symbols.TryGetValue(symbol, out info!);
    }

    public void Clear()
    {
        _symbols.Clear();
    }
}
