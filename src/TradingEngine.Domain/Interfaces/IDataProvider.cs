namespace TradingEngine.Domain;

public interface IDataProvider
{
    ITradeRepository Trades { get; }
    IEquityRepository Equity { get; }
    IOrderRepository Orders { get; }
    IEventLogRepository EventLog { get; }
    IBarRepository Bars { get; }
}
