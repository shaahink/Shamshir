namespace TradingEngine.Application;

public sealed class CrossRateStore
{
    public decimal GbpUsdRate { get; set; } = 1.2650m;
    public decimal UsdJpyRate { get; set; } = 149.50m;

    public decimal Convert(string from, string to)
    {
        if (from == to) return 1m;
        if (from == "JPY" && to == "USD") return 1m / UsdJpyRate;
        if (from == "USD" && to == "JPY") return UsdJpyRate;
        if (from == "GBP" && to == "USD") return GbpUsdRate;
        if (from == "USD" && to == "GBP") return 1m / GbpUsdRate;
        // F9 (iter-26): never silently return 1 for an unknown leg — that is a silent financial
        // error (wrong pip value → wrong lot size / PnL). Fail loud so a missing rate is caught.
        // All currently-seeded symbols are covered by the legs above; add the rate + leg before
        // trading a symbol quoted/based in a new currency.
        throw new InvalidOperationException(
            $"No cross rate available for {from}->{to}. Add the rate source and conversion leg to CrossRateStore.");
    }
}
