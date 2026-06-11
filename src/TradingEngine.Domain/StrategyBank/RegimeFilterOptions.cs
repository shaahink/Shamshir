namespace TradingEngine.Domain;

public record RegimeFilterOptions
{
    public bool AllowTrending { get; init; } = true;
    public bool AllowRanging { get; init; } = true;
    public bool AllowHighVolatility { get; init; } = true;
    public bool AllowLowVolatility { get; init; } = true;
    public bool AllowUnknown { get; init; } = true;
}

public static class RegimeFilterExtensions
{
    public static bool Allows(this RegimeFilterOptions filter, MarketRegime regime) => regime switch
    {
        MarketRegime.Trending => filter.AllowTrending,
        MarketRegime.Ranging => filter.AllowRanging,
        MarketRegime.HighVolatility => filter.AllowHighVolatility,
        MarketRegime.LowVolatility => filter.AllowLowVolatility,
        _ => filter.AllowUnknown,
    };
}
