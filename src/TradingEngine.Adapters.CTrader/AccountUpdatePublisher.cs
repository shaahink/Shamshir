using System;

namespace TradingEngine.Adapters.CTrader;

public class AccountUpdatePublisher
{
    private readonly PipeClient _pipe;

    public AccountUpdatePublisher(PipeClient pipe)
    {
        _pipe = pipe;
    }

    public void Publish(double balance, double equity, double floatingPnl, DateTime timestamp)
    {
        _pipe.Send(new PipeMessage
        {
            Type = "AccountUpdate",
            Payload = new
            {
                Balance = balance,
                Equity = equity,
                FloatingPnL = floatingPnl,
                TimestampUtc = timestamp.ToString("o")
            }
        });
    }
}
