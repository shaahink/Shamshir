namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class VenueSymbolSpecEntity
{
    public string Symbol { get; set; } = "";
    public string Broker { get; set; } = "";
    public string CapturedAtUtc { get; set; } = "";
    public double Commission { get; set; }
    public string CommissionType { get; set; } = "";
    public double SwapLong { get; set; }
    public double SwapShort { get; set; }
    public string SwapCalculationType { get; set; } = "";
    public double LotSize { get; set; }
    public double PipSize { get; set; }
    public double TickSize { get; set; }
    public double TickValue { get; set; }
    public int Digits { get; set; }
    public string TripleSwapDay { get; set; } = "";
    public double TypicalSpread { get; set; }
}
