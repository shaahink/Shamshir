namespace TradingEngine.Domain.Interfaces;

/// <summary>
/// P4.4 (F44): durable storage for venue-declared symbol economics (<see cref="VenueSymbolSpec"/>).
///
/// Only the cTrader leg has a cBot, so only the cTrader leg can LEARN the broker's real commission and
/// swap rates. P1 captured them into the in-memory <see cref="ISymbolInfoRegistry"/> and stopped there,
/// which left two holes: the rates died with the process, and the TAPE leg — which never sees a cBot —
/// silently fell back to the fabricated rates in symbols.json. That is how the tape modelled a EURUSD
/// long as EARNING 0.5/lot/night while the venue CHARGED 7.04, and why swap was the last thing standing
/// between us and a green parity gate.
///
/// Persisting the spec closes the loop: capture it once from the venue, and every later run on either
/// venue is priced off the same numbers. Same lesson as F32 (spread) and F34 (currency) — the two legs
/// must be fed from ONE source, and that source must be the venue.
/// </summary>
public interface IVenueSymbolSpecStore
{
    /// <summary>Upserts a spec captured from the venue (keyed on symbol + broker).</summary>
    Task SaveAsync(VenueSymbolSpec spec, CancellationToken ct = default);

    /// <summary>Every spec captured so far, for seeding the registry at startup.</summary>
    Task<IReadOnlyList<VenueSymbolSpec>> LoadAllAsync(CancellationToken ct = default);
}
