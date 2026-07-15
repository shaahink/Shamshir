namespace TradingEngine.Domain;

public interface ISymbolInfoRegistry
{
    SymbolInfo Get(Symbol symbol);
    void Register(SymbolInfo info);
    bool TryGet(Symbol symbol, out SymbolInfo info);

    /// <summary>Store a venue-captured symbol spec, replacing any prior spec for the same symbol+broker.
    /// The registry's <see cref="Get"/> will prefer venue-sourced economics over the static fallback.</summary>
    void UpsertVenueSpec(VenueSymbolSpec spec);

    /// <summary>Retrieve the most-recently-captured venue spec for a symbol, if any.</summary>
    bool TryGetVenueSpec(Symbol symbol, out VenueSymbolSpec spec);

    /// <summary>Returns true when at least one venue spec has been captured. Used to decide whether
    /// the static fallback warning is noisy or genuinely alarming.</summary>
    bool HasAnyVenueSpecs { get; }
}
