using System.Text.Json;

namespace TradingEngine.Host;

public sealed class SymbolCatalog
{
    private readonly Dictionary<string, SymbolInfo> _byName;

    public SymbolCatalog(string solutionRoot)
    {
        var path = Path.Combine(solutionRoot, "config", "symbols.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Symbols catalog not found: {path}");

        var json = File.ReadAllText(path);
        var entries = JsonSerializer.Deserialize<List<SymbolEntry>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Failed to deserialize symbols catalog");

        _byName = new Dictionary<string, SymbolInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            var symbol = Symbol.Parse(e.Symbol);
            var category = Enum.Parse<SymbolCategory>(e.Category, ignoreCase: true);
            _byName[e.Symbol] = new SymbolInfo(
                symbol, category,
                e.BaseCurrency, e.QuoteCurrency,
                (decimal)e.PipSize, (decimal)e.TickSize, (decimal)e.ContractSize,
                (decimal)e.MinLots, (decimal)e.MaxLots, (decimal)e.LotStep,
                (decimal)e.MarginRate, (decimal)e.TypicalSpread);
        }
    }

    public SymbolInfo Resolve(string symbolName)
    {
        if (_byName.TryGetValue(symbolName, out var info))
            return info;
        throw new KeyNotFoundException(
            $"Symbol '{symbolName}' not found in symbols catalog (config/symbols.json). " +
            $"Add it or correct the symbol name.");
    }

    public IReadOnlyList<SymbolInfo> ResolveAll(IReadOnlyList<string> names)
    {
        return names.Select(Resolve).ToList();
    }

    public IReadOnlyList<SymbolInfo> GetAll()
    {
        return _byName.Values.ToList();
    }

    private sealed record SymbolEntry(
        string Symbol,
        string Category,
        string BaseCurrency,
        string QuoteCurrency,
        double PipSize,
        double TickSize,
        double ContractSize,
        double MinLots,
        double MaxLots,
        double LotStep,
        double MarginRate,
        double TypicalSpread);
}
