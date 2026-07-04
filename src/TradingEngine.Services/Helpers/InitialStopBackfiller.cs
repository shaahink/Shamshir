using System.Text.Json;

namespace TradingEngine.Services.Helpers;

/// <summary>
/// P0.1 one-off backfill: recovers the ENTRY-time stop loss for trades persisted before
/// <c>PositionState.InitialStopLoss</c> existed, so their R-multiple can be recomputed honestly.
///
/// Primary source: the <c>Journal</c> table's <c>OrderProposed</c> row for the trade's (RunId, OrderId) —
/// the strategy's original StopLoss, unaffected by any later breakeven/trailing move. Verified against the
/// live DB: every existing trade has a matching OrderProposed row.
///
/// Fallback (only if a run predates the journal entirely): <c>EntrySnapshotJson.stopLoss</c>. This is NOT
/// reliable for trades whose stop was moved before close (see PROGRESS.md P0.1 deviation note) — resolutions
/// using it are tagged "snapshot-fallback" so callers can flag them as approximate.
/// </summary>
public static class InitialStopBackfiller
{
    public enum Source { Journal, SnapshotFallback, Unresolved }

    public sealed record Resolution(decimal? StopLoss, Source Source);

    /// <summary>Parses a run's OrderProposed journal rows into an OrderId → StopLoss map. Rows that
    /// aren't well-formed (missing OrderId/StopLoss) are silently skipped — the caller's per-trade
    /// resolution will simply fall through to the snapshot fallback or "unresolved".</summary>
    public static IReadOnlyDictionary<Guid, decimal> ParseOrderProposedStops(IEnumerable<string> orderProposedEventJson)
    {
        var map = new Dictionary<Guid, decimal>();
        foreach (var json in orderProposedEventJson)
        {
            if (string.IsNullOrWhiteSpace(json)) continue;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("OrderId", out var orderIdProp)) continue;
                if (!orderIdProp.TryGetGuid(out var orderId)) continue;
                if (!root.TryGetProperty("StopLoss", out var stopLossProp)) continue;
                if (!stopLossProp.TryGetProperty("Value", out var valueProp)) continue;
                map[orderId] = valueProp.GetDecimal();
            }
            catch (JsonException)
            {
                // Malformed row — skip, don't fail the whole batch.
            }
        }
        return map;
    }

    public static Resolution Resolve(Guid orderId, IReadOnlyDictionary<Guid, decimal> journalStopsForRun, string? entrySnapshotJson)
    {
        if (journalStopsForRun.TryGetValue(orderId, out var fromJournal))
            return new Resolution(fromJournal, Source.Journal);

        if (!string.IsNullOrWhiteSpace(entrySnapshotJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(entrySnapshotJson);
                if (doc.RootElement.TryGetProperty("stopLoss", out var sl) && sl.ValueKind == JsonValueKind.Number)
                    return new Resolution(sl.GetDecimal(), Source.SnapshotFallback);
            }
            catch (JsonException)
            {
                // fall through to Unresolved
            }
        }

        return new Resolution(null, Source.Unresolved);
    }
}
