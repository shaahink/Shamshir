namespace TradingEngine.Infrastructure.Events;

public static class JournalNormalizer
{
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

            // Persisted vocabulary (DecisionRecord/PipelineEvent)
            "OrderSubmitted" => nameof(JournalEventKind.ORDER),
            "OrderFilled" => nameof(JournalEventKind.FILL),
            "OrderRejected" => nameof(JournalEventKind.REJECTED),
            "BreachDetected" => nameof(JournalEventKind.BREACH),
            "GovernorStateChanged" => nameof(JournalEventKind.GOVERNOR),

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
