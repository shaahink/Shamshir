namespace TradingEngine.Domain;

public enum PositionPhase
{
    Intended,
    Submitted,
    Open,
    Reducing,
    Closing,
    Closed,
    Rejected,
    Cancelled
}
