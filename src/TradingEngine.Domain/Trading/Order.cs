namespace TradingEngine.Domain;

public record Order(
    Guid Id,
    TradeIntent Intent,
    decimal RequestedLots,
    OrderState State,
    Price? FillPrice,
    decimal FilledLots,
    DateTime CreatedAtUtc,
    DateTime? FilledAtUtc,
    string? RejectionReason)
{
    public bool IsTerminal =>
        State is OrderState.Filled or OrderState.Cancelled or OrderState.Rejected;
}
