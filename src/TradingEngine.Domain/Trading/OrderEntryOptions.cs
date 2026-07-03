namespace TradingEngine.Domain;

public record OrderEntryOptions
{
    public OrderEntryMethod Method { get; init; } = OrderEntryMethod.LimitOffset;
    public double LimitOffsetPips { get; init; } = 2.0;
    public double MaxSlippagePips { get; init; } = 2.0;
    public int LimitOrderExpiryBars { get; init; } = 3;
    public int MaxMarketRetries { get; init; } = 2;
}

public enum OrderEntryMethod { Market, LimitOffset, MarketWithSlippage }
