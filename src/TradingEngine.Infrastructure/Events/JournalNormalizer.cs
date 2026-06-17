namespace TradingEngine.Infrastructure.Events;

public static class JournalNormalizer
{
    // The reasons the lifecycle stamps on a close fill. When an "OrderFilled" record carries one of
    // these, it is a position CLOSE (SL/TP/forced), not an entry FILL — they share the same event name
    // but must read differently in the journal, or the CLOSE filter shows nothing and every close
    // hides under FILL.
    private static readonly HashSet<string> CloseReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "SL", "TP", "FORCE", "DailyDD", "MaxDD", "STOPOUT", "CLOSED", "MANUAL"
    };

    public static string? NormalizeKind(string eventName, string? reason)
    {
        return eventName switch
        {
            // Live vocabulary (BacktestProgressEvent)
            "SIGNAL" => nameof(JournalEventKind.SIGNAL),
            "ORDER" => nameof(JournalEventKind.ORDER),
            "EXEC" => nameof(JournalEventKind.FILL),
            "CLOSE" => nameof(JournalEventKind.CLOSE),
            "REJECTED" => nameof(JournalEventKind.REJECTED),
            "BREACH" => nameof(JournalEventKind.BREACH),
            "ENTRY_EXPIRED" => nameof(JournalEventKind.ENTRY_EXPIRED),
            "CANCELLED" => nameof(JournalEventKind.CANCELLED),

            // Persisted vocabulary (DecisionRecord/PipelineEvent)
            "OrderSubmitted" => nameof(JournalEventKind.ORDER),
            // An OrderFilled stamped with a close reason is a CLOSE; otherwise it is an entry FILL.
            "OrderFilled" => reason is not null && CloseReasons.Contains(reason)
                ? nameof(JournalEventKind.CLOSE)
                : nameof(JournalEventKind.FILL),
            "OrderPartiallyFilled" => nameof(JournalEventKind.FILL),
            "OrderRejected" => nameof(JournalEventKind.REJECTED),
            "OrderCancelled" => nameof(JournalEventKind.ENTRY_EXPIRED),
            "BreachDetected" => nameof(JournalEventKind.BREACH),
            "GovernorStateChanged" => nameof(JournalEventKind.GOVERNOR),
            "TradeClosed" => nameof(JournalEventKind.CLOSE),

            _ => InferFromReason(reason)
        };
    }

    private static string? InferFromReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return null;

        var r = reason;
        if (r.Contains("governor", StringComparison.OrdinalIgnoreCase))
            return nameof(JournalEventKind.GOVERNOR);
        if (r.Contains("entry expired", StringComparison.OrdinalIgnoreCase) || r.Contains("expire", StringComparison.OrdinalIgnoreCase))
            return nameof(JournalEventKind.ENTRY_EXPIRED);
        if (r.Contains("cancelled", StringComparison.OrdinalIgnoreCase) || r.Contains("canceled", StringComparison.OrdinalIgnoreCase))
            return nameof(JournalEventKind.CANCELLED);

        return null;
    }
}
