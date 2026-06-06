namespace TradingEngine.Domain;

public enum LotSizingMethod
{
    PercentRisk,
    FixedLots,
    FixedDollarRisk,
    KellyFraction,
    AntiMartingale,
}
