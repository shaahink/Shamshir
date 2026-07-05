namespace TradingEngine.Domain;

public record OrderEntryOptions
{
    public OrderEntryMethod Method { get; init; } = OrderEntryMethod.LimitOffset;
    public double LimitOffsetPips { get; init; } = 2.0;
    public double MaxSlippagePips { get; init; } = 2.0;
    public int LimitOrderExpiryBars { get; init; } = 3;
    public int MaxMarketRetries { get; init; } = 2;

    /// <summary>iter-quant-model P2.6 (D9): normalized replacement for <see cref="LimitOffsetPips"/> —
    /// an ATR fraction (the entry-fill wiggle room ought to scale with volatility, not a flat pip count).</summary>
    public double? LimitOffsetAtrFraction { get; init; }

    /// <summary>iter-quant-model P2.6 (D9): normalized replacement for <see cref="MaxSlippagePips"/> — a
    /// spread multiple (slippage tolerance is naturally spread-relative).</summary>
    public double? MaxSlippageSpreadMultiple { get; init; }
}

public enum OrderEntryMethod { Market, LimitOffset, MarketWithSlippage }
