namespace TradingEngine.Domain;

public enum JournalEventKind
{
    SIGNAL,
    ORDER,
    FILL,
    CLOSE,
    REJECTED,
    BREACH,
    GOVERNOR,
    ENTRY_EXPIRED,
    CANCELLED
}
