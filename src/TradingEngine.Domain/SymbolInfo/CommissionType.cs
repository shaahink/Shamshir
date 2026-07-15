namespace TradingEngine.Domain;

/// <summary>
/// How the venue computes commission. Mirrors cTrader's <c>SymbolCommissionType</c> enum with a
/// translation layer that lives in the cBot. The domain stays venue-agnostic.
/// </summary>
public enum CommissionType
{
    /// <summary>Not yet captured from any venue. Treated the same as <see cref="Unknown"/>.</summary>
    None = 0,

    /// <summary>Per-lot flat fee regardless of price. Commission = lots × rate × 2.</summary>
    AbsolutePerLot = 1,

    /// <summary>USD charged per million USD of notional volume per side.
    /// Commission = (lots × contractSize × priceToUsd) / 1_000_000 × rate × 2.</summary>
    UsdPerMillionUsdVolume = 2,

    /// <summary>Commission in price units (pips).</summary>
    Pips = 3,

    /// <summary>Commission as a percentage of notional value.</summary>
    PercentOfNotionalValue = 4,

    /// <summary>Venue reported a type we do not model. Falls back to AbsolutePerLot with a warning.</summary>
    Unknown = 99,
}
