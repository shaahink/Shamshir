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
        var payload = MessageSerializer.Serialize(new
        {
            Symbol = symbol,
            Bid = bid,
            Ask = ask,
            TimestampUtc = timestamp.ToString("o")
        });

        _pipe.Send(new PipeMessage
        {
            Type = "Tick",
            Payload = payload
        });
    }
}
