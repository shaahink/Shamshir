namespace TradingEngine.Domain;

public interface IEntryFilter
{
    bool Allows(MarketContext context);
}
