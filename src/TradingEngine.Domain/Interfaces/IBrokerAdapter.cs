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
    Task ClosePartialPositionAsync(Guid positionId, decimal lots, CancellationToken ct)
        => ClosePositionAsync(positionId, ct); // default: full close

    Task<AccountState> GetAccountStateAsync(CancellationToken ct);
    Task ConnectAsync(CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
    bool IsConnected { get; }

    Task CompleteBarAsync(long seq, CancellationToken ct) => Task.CompletedTask;

    // -- Venue hooks (default no-ops) so the engine never type-sniffs concrete adapters. --

    /// <summary>Register a callback fired whenever the venue (re)connects. Only stateful
    /// transports (cTrader) need it; others ignore it.</summary>
    void RegisterConnectedHandler(Action handler) { }

    /// <summary>Observe each processed tick. A simulated venue uses this to drive fills against
    /// resting orders; live/replay venues ignore it.</summary>
    void OnTickObserved(Tick tick) { }

    /// <summary>Observe each processed bar. A replay venue uses this to advance its clock and
    /// last price so fills price correctly; others ignore it.</summary>
    void OnBarObserved(Bar bar) { }

    /// <summary>Signal the engine finished a bar (lock-step venues block the feed until this).
    /// The adapter supplies its own sequence; non-lock-step venues are a no-op.</summary>
    Task CompleteBarAsync(CancellationToken ct) => Task.CompletedTask;
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
    DateTime TimestampUtc)
{
    public decimal? GrossProfit { get; init; }
    public decimal? NetProfit { get; init; }
    public decimal? Commission { get; init; }
    public decimal? Swap { get; init; }
}

public record OrderRequest(
    TradeIntent Intent,
    decimal Lots,
    Symbol Symbol,
    TradeDirection Direction,
    OrderType Type,
    Price? LimitPrice);
