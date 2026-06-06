namespace TradingEngine.Infrastructure.Persistence;

public sealed class SqliteDataProvider : IDataProvider
{
    public SqliteDataProvider(
        ITradeRepository trades,
        IOrderRepository orders,
        IEquityRepository equity,
        IEventLogRepository eventLog,
        IBarRepository bars)
    {
        Trades = trades;
        Orders = orders;
        Equity = equity;
        EventLog = eventLog;
        Bars = bars;
    }

    public ITradeRepository Trades { get; }
    public IEquityRepository Equity { get; }
    public IOrderRepository Orders { get; }
    public IEventLogRepository EventLog { get; }
    public IBarRepository Bars { get; }
}
