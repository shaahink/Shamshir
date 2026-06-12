namespace TradingEngine.Domain.Events;

public record GovernorStateChanged(
    GovernorTradingState From,
    GovernorTradingState To,
    string Reason,
    DateTime AtUtc) : EngineEvent(AtUtc);
