namespace TradingEngine.Domain;

public record CurrencyExposureSnapshot
{
    public static readonly CurrencyExposureSnapshot Empty = new();
    public IReadOnlyDictionary<string, decimal> NetRiskByCurrency { get; init; } =
        new Dictionary<string, decimal>();
    public decimal TotalCorrelatedRisk { get; init; }
}
