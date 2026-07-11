using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TradingEngine.Infrastructure;

public sealed class SymbolInfoRegistry : ISymbolInfoRegistry
{
    private readonly ConcurrentDictionary<Symbol, SymbolInfo> _symbols = new();
    private readonly ConcurrentDictionary<Symbol, VenueSymbolSpec> _venueSpecs = new();
    private readonly ILogger<SymbolInfoRegistry> _logger;

    public bool HasAnyVenueSpecs => !_venueSpecs.IsEmpty;

    public SymbolInfoRegistry()
        : this(NullLogger<SymbolInfoRegistry>.Instance) { }

    public SymbolInfoRegistry(ILogger<SymbolInfoRegistry> logger)
    {
        _logger = logger;
    }

    public SymbolInfo Get(Symbol symbol)
    {
        if (_symbols.TryGetValue(symbol, out var info))
            return info;
        throw new KeyNotFoundException($"Symbol {symbol} is not registered. Ensure config/symbols/defaults.json is loaded.");
    }

    public void Register(SymbolInfo info)
    {
        // Check if a venue spec already exists for this symbol; if so, override
        // the static economics with venue-sourced values.
        if (_venueSpecs.TryGetValue(info.Symbol, out var spec))
        {
            var merged = MergeVenueSpec(info, spec);
            _symbols[info.Symbol] = merged;
            return;
        }

        _symbols[info.Symbol] = info;

        if (!HasAnyVenueSpecs)
        {
            _logger.LogWarning(
                "SYMBOL_FALLBACK No venue symbol spec captured for {Symbol} (or any symbol yet). " +
                "Using static symbols.json values — commission and swap may not match the broker. " +
                "Run a cTrader backtest with this symbol to capture venue economics.",
                info.Symbol);
        }
    }

    public bool TryGet(Symbol symbol, out SymbolInfo info)
    {
        return _symbols.TryGetValue(symbol, out info!);
    }

    public void UpsertVenueSpec(VenueSymbolSpec spec)
    {
        _venueSpecs[spec.Symbol] = spec;

        if (_symbols.TryGetValue(spec.Symbol, out var existing))
        {
            _symbols[spec.Symbol] = MergeVenueSpec(existing, spec);
        }
    }

    public bool TryGetVenueSpec(Symbol symbol, out VenueSymbolSpec spec)
    {
        return _venueSpecs.TryGetValue(symbol, out spec!);
    }

    public void Clear()
    {
        _symbols.Clear();
        _venueSpecs.Clear();
    }

    private static SymbolInfo MergeVenueSpec(SymbolInfo existing, VenueSymbolSpec spec)
    {
        return existing with
        {
            CommissionPerLotPerSide = spec.Commission,
            CommissionType = spec.CommissionType,
            SwapLongPerLotPerNight = spec.SwapLong,
            SwapShortPerLotPerNight = spec.SwapShort,
            ContractSize = spec.LotSize,
            PipSize = spec.PipSize,
            TickSize = spec.TickSize,
            TypicalSpread = spec.TypicalSpread,
            TripleSwapWeekday = spec.TripleSwapDay.ToString(),
        };
    }
}
