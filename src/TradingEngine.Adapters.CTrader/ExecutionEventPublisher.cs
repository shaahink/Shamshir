using System;

namespace TradingEngine.Adapters.CTrader;

public class ExecutionEventPublisher
{
    private readonly PipeClient _pipe;

    public ExecutionEventPublisher(PipeClient pipe)
    {
        _pipe = pipe;
    }

    public void Publish(Guid orderId, string newState, double? fillPrice, double filledLots, string? rejectionReason, DateTime timestamp)
    {
        _pipe.Send(new PipeMessage
        {
            Type = "ExecutionEvent",
            Payload = new
            {
                OrderId = orderId,
                NewState = newState,
                FillPrice = fillPrice,
                FilledLots = filledLots,
                RejectionReason = rejectionReason,
                TimestampUtc = timestamp.ToString("o")
            }
        });
    }
}
