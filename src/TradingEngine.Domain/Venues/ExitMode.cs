namespace TradingEngine.Domain;

/// <summary>
/// Who owns exit execution for a venue.
///
/// <see cref="EngineSimulated"/> — the engine detects SL/TP hits bar-by-bar and emits
/// <see cref="CloseOpenPosition"/> effects. Used by venues that have no real broker stops
/// (e.g. legacy replay before the venue-owned model was adopted).
///
/// <see cref="VenueManaged"/> — the venue owns exits: it sets real broker SL/TP on entry,
/// triggers them server-side when hit, and reports each close back with a reason (SL/TP/STOPOUT/…).
/// The engine NEVER emits a <see cref="CloseOpenPosition"/> for exit — it reconciles its book to the
/// venue's open set every bar. Both cTrader and the unified replay adapter use this model.
/// </summary>
public enum ExitMode
{
    EngineSimulated = 0,
    VenueManaged = 1,
}
