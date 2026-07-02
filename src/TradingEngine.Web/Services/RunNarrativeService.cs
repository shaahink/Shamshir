using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Web.Services;

public sealed class RunNarrativeService
{
    private readonly TradingDbContext _db;

    public RunNarrativeService(TradingDbContext db) => _db = db;

    public async Task<NarrativeResponse> GetNarrativeAsync(
        string runId, long? afterSeq, string[]? kinds, string? severity, int limit = 100, CancellationToken ct = default)
    {
        var query = _db.JournalEntries.AsNoTracking().Where(e => e.RunId == runId);
        if (afterSeq.HasValue) query = query.Where(e => e.Seq > afterSeq.Value);

        var skipDefault = kinds is null || kinds.Length == 0;
        if (skipDefault)
            query = query.Where(e => e.EventKind != "BarClosed" && e.EventKind != "EquityObserved");

        var entries = await query.OrderBy(e => e.Seq).Take(limit).ToListAsync(ct);
        var events = entries.Select(BuildNarrative).ToList();
        var maxSeq = events.Count > 0 ? events.Max(e => e.Seq) : afterSeq ?? 0;
        return new NarrativeResponse(events, maxSeq, events.Count == limit);
    }

    private static NarrativeEvent BuildNarrative(JournalEntryEntity entry)
    {
        return entry.EventKind switch
        {
            "OrderProposed" => BuildOrder(entry),
            "OrderFilled" => BuildFill(entry),
            "StopLossModifyRequested" => BuildTrail(entry),
            "OrderRejected" => BuildRejection(entry),
            "Breach" => new(entry.Seq, entry.SimTimeUtc, "critical", "Risk", "BREACH", entry.EventJson ?? ""),
            "OrderCancelled" => BuildCancelled(entry),
            "DayRoll" => new(entry.Seq, entry.SimTimeUtc, "info", "System", "New prop-firm day", ""),
            "BarClosed" => BuildBar(entry),
            "EquityObserved" => BuildEquity(entry),
            _ => new(entry.Seq, entry.SimTimeUtc, "info", "System", entry.EventKind, entry.EventJson ?? ""),
        };
    }

    private static NarrativeEvent BuildOrder(JournalEntryEntity e)
    {
        var json = e.EventJson ?? "{}";
        using var d = JsonDocument.Parse(json);
        var r = d.RootElement;
        var s = S(r, "strategyId") ?? "?";
        var sym = S(r, "symbol") ?? "";
        var dir = S(r, "direction") ?? "";
        var ep = D(r, "entryPrice");
        var sl = D(r, "stopLoss");
        var tp = D(r, "takeProfit");
        var lots = D(r, "lots");
        var rp = D(r, "riskPercent");
        var ok = S(r, "accepted") == "true";

        if (!ok)
            return new(e.Seq, e.SimTimeUtc, "warning", "Signal", $"{s}: {dir} {sym} rejected", S(r, "rejectionReason") ?? "");

        var detail = ep.HasValue ? $"@{ep.Value:F5}" : "";
        if (sl.HasValue && ep.HasValue)
        {
            var pips = dir == "LONG" ? (ep.Value - sl.Value) / 0.0001m : (sl.Value - ep.Value) / 0.0001m;
            detail += $" - SL {sl.Value:F5} ({pips:F0}p)";
        }
        if (tp.HasValue) detail += $", TP {tp.Value:F5}";
        if (lots.HasValue) detail += $", {lots.Value:F2} lots";
        if (rp.HasValue) detail += $" ({rp.Value:P1} risk)";
        return new(e.Seq, e.SimTimeUtc, "action", "Signal", $"{s} {dir} {sym}", detail);
    }

    private static NarrativeEvent BuildFill(JournalEntryEntity e)
    {
        var json = e.EventJson ?? "{}";
        using var d = JsonDocument.Parse(json);
        var r = d.RootElement;
        var sym = S(r, "symbol") ?? "";
        var dir = S(r, "direction") ?? "";
        var fp = D(r, "fillPrice");
        var lots = D(r, "lots");
        var cr = S(r, "closeReason");
        var net = D(r, "netProfit");
        var rm = D(r, "rMultiple");

        if (cr is not null)
        {
            var label = cr switch { "SL" => "stop-loss hit", "TP" => "take-profit hit", "FORCE" => "force-closed", "DailyDD" => "daily-DD breach", "MaxDD" => "max-DD breach", _ => cr };
            var detail = fp.HasValue ? $"@{fp.Value:F5}" : "";
            if (net.HasValue) detail += $", {net.Value:C2} net";
            if (rm.HasValue) detail += $" ({rm.Value:F1}R)";
            return new(e.Seq, e.SimTimeUtc, "action", "Exit", $"Closed {dir} {sym} - {label}", detail);
        }
        var d2 = fp.HasValue ? $"@{fp.Value:F5}" : "";
        if (lots.HasValue) d2 += $", {lots.Value:F2} lots";
        return new(e.Seq, e.SimTimeUtc, "action", "Entry", $"Opened {dir} {sym}", d2);
    }

    private static NarrativeEvent BuildTrail(JournalEntryEntity e)
    {
        var json = e.EventJson ?? "{}";
        using var d = JsonDocument.Parse(json);
        var r = d.RootElement;
        var oldSl = D(r, "oldStopLoss");
        var newSl = D(r, "newStopLoss");
        var mode = S(r, "mode") ?? "TRAIL";
        if (oldSl.HasValue && newSl.HasValue)
        {
            var diff = newSl.Value - oldSl.Value;
            return new(e.Seq, e.SimTimeUtc, "info", "AddOn", $"Trailed stop {oldSl.Value:F5} -> {newSl.Value:F5} {(diff >= 0 ? "^" : "v")}", $"{mode}, D{Math.Abs(diff):F5}");
        }
        return new(e.Seq, e.SimTimeUtc, "info", "AddOn", $"Stop modified ({mode})", "");
    }

    private static NarrativeEvent BuildRejection(JournalEntryEntity e)
    {
        var reason = "";
        if (!string.IsNullOrWhiteSpace(e.EventJson))
        {
            using var d = JsonDocument.Parse(e.EventJson);
            reason = S(d.RootElement, "reason") ?? "";
        }
        return new(e.Seq, e.SimTimeUtc, "warning", "Risk", "Signal rejected", reason);
    }

    private static NarrativeEvent BuildCancelled(JournalEntryEntity e)
    {
        var reason = "ENTRY_EXPIRED";
        if (!string.IsNullOrWhiteSpace(e.EventJson))
        {
            using var d = JsonDocument.Parse(e.EventJson);
            reason = S(d.RootElement, "reason") ?? reason;
        }
        return new(e.Seq, e.SimTimeUtc, "info", "Signal", "Order cancelled", reason);
    }

    private static NarrativeEvent BuildBar(JournalEntryEntity e)
    {
        var json = e.EventJson ?? "{}";
        using var d = JsonDocument.Parse(json);
        var sym = S(d.RootElement, "symbol") ?? "";
        var close = D(d.RootElement, "close");
        return new(e.Seq, e.SimTimeUtc, "info", "System", $"Bar closed {sym}", close.HasValue ? $"@{close.Value:F5}" : "");
    }

    private static NarrativeEvent BuildEquity(JournalEntryEntity e)
    {
        var json = e.EventJson ?? "{}";
        using var d = JsonDocument.Parse(json);
        var eq = D(d.RootElement, "equity");
        var bal = D(d.RootElement, "balance");
        return new(e.Seq, e.SimTimeUtc, "info", "System", "Equity snapshot", eq.HasValue ? $"{eq.Value:C2} (bal {bal:C2})" : "");
    }

    private static string? S(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static decimal? D(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;
}

public sealed record NarrativeEvent(long Seq, DateTime SimTime, string Severity, string Category, string Headline, string Detail);
public sealed record NarrativeResponse(IReadOnlyList<NarrativeEvent> Events, long LatestSeq, bool HasMore);
