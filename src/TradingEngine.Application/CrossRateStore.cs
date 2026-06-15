namespace TradingEngine.Application;

public sealed class CrossRateStore
{
    public decimal GbpUsdRate { get; set; } = 1.2650m;
    public decimal UsdJpyRate { get; set; } = 149.50m;

    public decimal Convert(string from, string to)
    {
        if (from == "JPY" && to == "USD") return 1m / UsdJpyRate;
        if (from == "GBP" && to == "USD") return GbpUsdRate;
        return 1m;
    }
}
