namespace TradingEngine.Domain;

public record EntryFilterOptions
{
    public bool Enabled { get; init; }
    public decimal MaxSpreadPips { get; init; }
    public decimal MaxAtrPips { get; init; }
    public string AtrIndicatorKey { get; init; } = "ATR14";
}
