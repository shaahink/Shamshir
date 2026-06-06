namespace TradingEngine.Domain;

public interface IExitBehavior
{
    Price ComputeStopLoss(Price entry, TradeDirection dir, MarketContext context, SymbolInfo sym);
    Price? ComputeTakeProfit(Price entry, Price sl, TradeDirection dir, MarketContext context, SymbolInfo sym);
}
