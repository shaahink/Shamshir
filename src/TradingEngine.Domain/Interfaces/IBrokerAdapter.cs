using System.Threading.Channels;

namespace TradingEngine.Domain;

public interface IBrokerAdapter
{
    ChannelReader<Tick> TickStream { get; }
    ChannelReader<Bar> BarStream { get; }
    ChannelReader<AccountUpdate> AccountStream { get; }
    ChannelReader<ExecutionEvent> ExecutionStream { get; }

    DateTime BrokerTimeUtc { get; }

    Task<Guid> SubmitOrderAsync(OrderRequest request, CancellationToken ct);
    Task ModifyOrderAsync(Guid orderId, Price newStopLoss, Price? newTakeProfit, CancellationToken ct);
    Task CancelOrderAsync(Guid orderId, CancellationToken ct);
    Task ClosePositionAsync(Guid positionId, CancellationToken ct);

    Task<AccountState> GetAccountStateAsync(CancellationToken ct);
    Task ConnectAsync(CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
    bool IsConnected { get; }
}

public record AccountState(decimal Balance, decimal Equity, IReadOnlyList<OpenPositionInfo> OpenPositions);

public record OpenPositionInfo(Guid PositionId, Symbol Symbol, TradeDirection Direction, decimal Lots, Price EntryPrice, Price CurrentStopLoss, Price? TakeProfit);

public record AccountUpdate(
    decimal Balance,
    decimal Equity,
    decimal FloatingPnL,
    DateTime TimestampUtc);

public record ExecutionEvent(
    Guid OrderId,
    OrderState NewState,
    Price? FillPrice,
    decimal FilledLots,
    string? RejectionReason,
    DateTime TimestampUtc);

public record OrderRequest(
    TradeIntent Intent,
    decimal Lots,
    Symbol Symbol,
    TradeDirection Direction,
    OrderType Type,
    Price? LimitPrice);
