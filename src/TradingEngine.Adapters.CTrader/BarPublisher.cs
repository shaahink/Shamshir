using System;

namespace TradingEngine.Adapters.CTrader;

public class BarPublisher
{
    private readonly PipeClient _pipe;

    public BarPublisher(PipeClient pipe)
    {
        _pipe = pipe;
    }

    public void Publish(string symbol, string timeframe, DateTime openTime,
        double open, double high, double low, double close, double volume)
    {
        _pipe.Send(new PipeMessage
        {
            Type = "Bar",
            Payload = new
            {
                Symbol = symbol,
                Timeframe = timeframe,
                OpenTimeUtc = openTime.ToString("o"),
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = volume
            }
        });
    }
}
