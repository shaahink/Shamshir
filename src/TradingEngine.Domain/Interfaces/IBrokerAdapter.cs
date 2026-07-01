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

    /// <summary>
    /// Close a full position at a caller-supplied exit price. Used by the backtest path so an
    /// engine-detected SL/TP exit fills at the stop/target price (F2/D3 iter-26) instead of the bar
    /// close. Live venues fill server-side at the real market price, so the default ignores the hint
    /// and routes to the normal market close.
    /// </summary>
    Task ClosePositionAtAsync(Guid positionId, Price exitPrice, CancellationToken ct)
        => ClosePositionAsync(positionId, ct);

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

    /// <summary>Register a callback fired when the venue reports its open-position snapshot
    /// (on connect and on every reconnect). The engine uses it to seed/resync its position
    /// tracker from positions that already exist at the venue (V1/V2). No-op for venues that
    /// have no out-of-band positions (simulated/replay).</summary>
    void RegisterReconcileHandler(Action<AccountState> handler) { }

    /// <summary>Register a callback fired when the venue confirms a stop-loss/take-profit
    /// modification, so the engine can write the venue-authoritative SL/TP back onto its
    /// position state (V3 — trailing). No-op for fire-and-forget venues.</summary>
    void RegisterStopModifiedHandler(Action<Guid, Price, Price?> handler) { }

    /// <summary>Register a callback fired when the venue reports a session handshake with metadata
    /// (symbol, period, mode, date range). Used by listen-mode capture (iter-ctrader-capture) to
    /// mint a RunId from a desktop-cTrader-launched session. No-op for venues that don't support it.</summary>
    void RegisterSessionStartedHandler(Action<SessionInfo> handler) { }

    /// <summary>Observe each processed tick. A simulated venue uses this to drive fills against
    /// resting orders; live/replay venues ignore it.</summary>
    void OnTickObserved(Tick tick) { }

    /// <summary>Observe each processed bar. A replay venue uses this to advance its clock and
    /// last price so fills price correctly; others ignore it.</summary>
    void OnBarObserved(Bar bar) { }

    /// <summary>Signal the engine finished a bar (lock-step venues block the feed until this).
    /// The adapter supplies its own sequence; non-lock-step venues are a no-op.</summary>
    Task CompleteBarAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Who owns exit execution. <see cref="ExitMode.VenueManaged"/> means the venue holds real broker
    /// stops and reports closes with a reason — the engine never detects exits bar-by-bar. Default is
    /// <see cref="ExitMode.EngineSimulated"/> for backward compatibility.
    /// </summary>
    ExitMode ExitMode => ExitMode.EngineSimulated;

    /// <summary>
    /// The venue's authoritative set of currently open position ids. Used for per-bar reconciliation:
    /// the engine compares its live book to this set and force-resolves any kernel position the venue
    /// no longer reports as open. Returns an empty set by default (no reconciliation for venues that
    /// don't support it).
    /// </summary>
    IReadOnlySet<Guid> GetOpenPositionIds() => new HashSet<Guid>();
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

    /// <summary>The instrument this execution belongs to (iter-37 K-GAP-6). Carried by the venue so the
    /// feedback bridge attributes fills/closes to the CORRECT symbol on a multi-symbol run instead of the
    /// old first-open-position / EURUSD guess. Null = unknown (the kernel falls back to resolving by id).</summary>
    public Symbol? Symbol { get; init; }

    /// <summary>Venue-authoritative reason a position was closed (SL / TP / STOPOUT / CLOSED),
    /// for venue-initiated (server-side SL/TP) closes the engine didn't request. Null for fills,
    /// rejections, and engine-requested closes (the engine already knows those reasons).</summary>
    public string? CloseReason { get; init; }
}

public record OrderRequest(
    TradeIntent Intent,
    decimal Lots,
    Symbol Symbol,
    TradeDirection Direction,
    OrderType Type,
    Price? LimitPrice,
    // The engine's own order id (= kernel PositionId). When set, a venue uses it as the order/position id
    // instead of minting its own, so the kernel's SubmitOrder/CloseOpenPosition/feedback all key off ONE id
    // (iter-36 K2 — the kernel is the authority for position identity; PositionId == OrderId). Null = the
    // venue mints its own id (the legacy imperative path, which captures the returned id).
    Guid? ClientOrderId = null);
