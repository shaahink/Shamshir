namespace TradingEngine.Domain;

/// <summary>
/// Symbol-level economics captured from a live venue (cTrader) at connection time. This is the
/// canonical source for commission, swap, and instrument metadata — <c>symbols.json</c> is a
/// loudly-warned fallback only (D10).
/// </summary>
/// <param name="Symbol">The symbol this spec applies to.</param>
/// <param name="Broker">Human-readable broker label (e.g. "ICMarkets-Demo").</param>
/// <param name="CapturedAtUtc">When the cBot emitted this spec.</param>
/// <param name="Commission">Broker's commission rate in its native units (see <see cref="CommissionType"/>).</param>
/// <param name="CommissionType">How to interpret <paramref name="Commission"/>.</param>
/// <param name="SwapLong">Raw swap rate for long positions (venue units). Sign: positive = the trader
///     PAYS; negative = the trader RECEIVES.</param>
/// <param name="SwapShort">Raw swap rate for short positions (venue units).</param>
/// <param name="SwapCalculationType">How to interpret swap rates (points, pips, percent, absolute).</param>
/// <param name="LotSize">Units per standard lot (100k for FX, 100 for XAU, 1 for BTC).</param>
/// <param name="PipSize">Price increment of one pip (0.0001 for EURUSD, 0.01 for XAUUSD).</param>
/// <param name="TickSize">Smallest price increment.</param>
/// <param name="TickValue">Monetary value of one tick per lot in account currency.</param>
/// <param name="Digits">Number of decimal places in the price display.</param>
/// <param name="TripleSwapDay">Day of week on which triple swap is charged.</param>
/// <param name="TypicalSpread">Typical spread in price units (ask − bid).</param>
public sealed record VenueSymbolSpec(
    Symbol Symbol,
    string Broker,
    DateTime CapturedAtUtc,
    decimal Commission,
    CommissionType CommissionType,
    decimal SwapLong,
    decimal SwapShort,
    string SwapCalculationType,
    decimal LotSize,
    decimal PipSize,
    decimal TickSize,
    decimal TickValue,
    int Digits,
    DayOfWeek TripleSwapDay,
    decimal TypicalSpread);
