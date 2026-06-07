using System;

namespace TradingEngine.Adapters.CTrader;

public class TickPublisher
{
    private readonly PipeClient _pipe;

    public TickPublisher(PipeClient pipe)
    {
        _pipe = pipe;
    }

    public void Publish(string symbol, double bid, double ask, DateTime timestamp)
    {
        _pipe.Send(new PipeMessage
        {
            Type = "Tick",
            Payload = new
            {
                Symbol = symbol,
                Bid = bid,
                Ask = ask,
                TimestampUtc = timestamp.ToString("o")
            }
        });
    }
}
