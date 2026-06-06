namespace TradingEngine.Domain;

public interface ISlTpCalculator
{
    Price CalculateStopLoss(
        Price entryPrice,
        TradeDirection direction,
        SlMethod method,
        SlParameters parameters,
        IReadOnlyList<Bar> recentBars);

    Price? CalculateTakeProfit(
        Price entryPrice,
        Price stopLoss,
        TradeDirection direction,
        TpMethod method,
        TpParameters parameters);
}

public enum SlMethod { FixedPips, AtrMultiple, SwingBased }
public enum TpMethod { None, FixedPips, RRMultiple, AtrMultiple }
