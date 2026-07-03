using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Web.Services;

/// <summary>
/// Projects the persisted StepRecord journal (<see cref="JournalEntryEntity"/>) into human-readable
/// narrative lines for the live monitor + report (M3.1/M3.2).
///
/// IMPORTANT — reads the REAL <c>EventJson</c> shape. The sink (<c>SqliteStepRecordSink</c>) serializes
/// each event's <c>RawEvent</c> with <em>PascalCase</em> property names and a <c>JsonStringEnumConverter</c>,
/// so an <see cref="OrderProposed"/> is <c>{"Symbol":{"Value":"EURUSD"},"Direction":"Long",...}</c> —
/// value objects (Symbol/Price) are nested <c>{"Value":..}</c> objects, enums are their member name
/// ("Long"/"Short"), NOT camelCase and NOT the invented fields an earlier draft assumed. The property
/// readers below are case-insensitive and unwrap the {Value} envelope so the projection survives a future
/// serializer casing change.
///
/// The <c>EventKind</c> is <c>evt.GetType().Name</c> EXCEPT for add-on events, which
/// <c>KernelBacktestLoop.EventKindFor</c> remaps to the canonical <see cref="AddOnJournalKinds"/> vocab:
/// a <see cref="StopLossModifyRequested"/> is journaled as its <c>Kind</c> ("TRAIL"/"BREAKEVEN"/"RIDE"),
/// a <see cref="PartialCloseRequested"/> as "PARTIAL", an <see cref="AddOnsResolved"/> as "ADDON_RESOLVED".
///
/// Accept vs reject for a proposal is NOT in the event JSON — the accepted path stamps
/// <c>DecisionReason = "Accepted"</c> (PositionLifecycle.HandleIntendedSubmitted), the rejected path stamps
/// the gate's reject reason. So <c>DecisionReason</c> that is neither null nor "Accepted" ⇒ rejected.
/// </summary>
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
        else
            query = query.Where(e => kinds!.Contains(e.EventKind));

        var entries = await query.OrderBy(e => e.Seq).Take(limit).ToListAsync(ct);
        var events = entries.Select(BuildNarrative).ToList();
        if (!string.IsNullOrWhiteSpace(severity))
            events = events.Where(e => string.Equals(e.Severity, severity, StringComparison.OrdinalIgnoreCase)).ToList();
        var maxSeq = entries.Count > 0 ? entries.Max(e => e.Seq) : afterSeq ?? 0;
        return new NarrativeResponse(events, maxSeq, entries.Count == limit);
    }

    /// <summary>Pure projection of one journal row → one narrative line. Public for unit testing.</summary>
    public static NarrativeEvent BuildNarrative(JournalEntryEntity entry)
    {
        return entry.EventKind switch
        {
            "OrderProposed" => BuildOrder(entry),
            "OrderFilled" => BuildFill(entry),
            "OrderRejected" => BuildRejection(entry),
            "OrderCancelled" => BuildCancelled(entry),
            AddOnJournalKinds.Trail or AddOnJournalKinds.Breakeven or AddOnJournalKinds.Ride => BuildTrail(entry),
            AddOnJournalKinds.Partial => BuildPartial(entry),
            AddOnJournalKinds.AddOnsResolved => new(entry.Seq, entry.SimTimeUtc, "info", "AddOn", "Add-ons resolved", ""),
            "Breach" => new(entry.Seq, entry.SimTimeUtc, "critical", "Risk", "BREACH", entry.EventJson ?? ""),
            "DayRolled" => new(entry.Seq, entry.SimTimeUtc, "info", "System", "New prop-firm day", ""),
            "WeekRolled" => new(entry.Seq, entry.SimTimeUtc, "info", "System", "New week", ""),
            "MonthRolled" => new(entry.Seq, entry.SimTimeUtc, "info", "System", "New month", ""),
            "BarClosed" => BuildBar(entry),
            "EquityObserved" => BuildEquity(entry),
            _ => new(entry.Seq, entry.SimTimeUtc, "info", "System", entry.EventKind, ""),
        };
    }

    private static NarrativeEvent BuildOrder(JournalEntryEntity e)
    {
        var r = Root(e.EventJson);
        var s = Str(r, "strategyId") ?? "?";
        var sym = SymOf(r);
        var dir = (Str(r, "direction") ?? "").ToUpperInvariant();
        var entry = Dec(r, "signalPriceMid");
        var sl = PriceOf(r, "stopLoss");
        var tp = PriceOf(r, "takeProfit");
        var slPips = Dec(r, "slPips");

        // Rejected ⇒ DecisionReason carries the gate reason; accepted ⇒ "Accepted"; direct-construct ⇒ null.
        var reason = e.DecisionReason;
        var rejected = !string.IsNullOrWhiteSpace(reason) && reason != "Accepted";
        if (rejected)
            return new(e.Seq, e.SimTimeUtc, "warning", "Signal", $"{s}: {dir} {sym} rejected", reason!);

        var detail = entry.HasValue ? $"@{entry.Value:F5}" : "";
        if (sl.HasValue)
        {
            detail += $" · SL {sl.Value:F5}";
            if (slPips.HasValue) detail += $" ({slPips.Value:F0}p)";
        }
        if (tp.HasValue) detail += $" · TP {tp.Value:F5}";
        return new(e.Seq, e.SimTimeUtc, "action", "Signal", $"{s} {dir} {sym}".Trim(), detail.Trim(' ', '·'));
    }

    private static NarrativeEvent BuildFill(JournalEntryEntity e)
    {
        var r = Root(e.EventJson);
        var sym = SymOf(r);
        var fp = PriceOf(r, "fillPrice");
        var lots = Dec(r, "filledLots");
        var cr = Str(r, "closeReason");
        var net = Dec(r, "netProfit");

        if (!string.IsNullOrEmpty(cr))
        {
            var label = cr switch
            {
                "SL" => "stop-loss hit",
                "TP" => "take-profit hit",
                "FORCE" => "force-closed",
                "STOPOUT" => "stopped out",
                "CLOSED" => "closed",
                "DailyDD" => "daily-DD breach",
                "MaxDD" => "max-DD breach",
                _ => cr,
            };
            var d = fp.HasValue ? $"@{fp.Value:F5}" : "";
            if (net.HasValue) d += (d.Length > 0 ? ", " : "") + $"{net.Value:C2} net";
            var severity = cr is "FORCE" or "STOPOUT" or "DailyDD" or "MaxDD" ? "warning" : "action";
            return new(e.Seq, e.SimTimeUtc, severity, "Exit", $"Closed {sym} — {label}", d);
        }

        var d2 = fp.HasValue ? $"@{fp.Value:F5}" : "";
        if (lots.HasValue) d2 += (d2.Length > 0 ? ", " : "") + $"{lots.Value:F2} lots";
        return new(e.Seq, e.SimTimeUtc, "action", "Entry", $"Opened {sym}", d2);
    }

    private static NarrativeEvent BuildTrail(JournalEntryEntity e)
    {
        var r = Root(e.EventJson);
        var newSl = PriceOf(r, "newStopLoss");
        var kind = e.EventKind;
        var verb = kind switch
        {
            AddOnJournalKinds.Breakeven => "Moved stop to break-even",
            AddOnJournalKinds.Ride => "Ride-stop moved",
            _ => "Trailed stop",
        };
        var detail = newSl.HasValue ? $"→ {newSl.Value:F5}" : "";
        return new(e.Seq, e.SimTimeUtc, "info", "AddOn", verb, detail);
    }

    private static NarrativeEvent BuildPartial(JournalEntryEntity e)
    {
        var r = Root(e.EventJson);
        var lots = Dec(r, "closeLots");
        var reason = Str(r, "reason") ?? "";
        return new(e.Seq, e.SimTimeUtc, "info", "Exit",
            lots.HasValue ? $"Partial close {lots.Value:F2} lots" : "Partial close", reason);
    }

    private static NarrativeEvent BuildRejection(JournalEntryEntity e)
    {
        var r = Root(e.EventJson);
        var sym = SymOf(r);
        var reason = Str(r, "reason") ?? e.DecisionReason ?? "";
        return new(e.Seq, e.SimTimeUtc, "warning", "Risk",
            string.IsNullOrEmpty(sym) ? "Order rejected" : $"Order rejected {sym}", reason);
    }

    private static NarrativeEvent BuildCancelled(JournalEntryEntity e)
    {
        var r = Root(e.EventJson);
        var sym = SymOf(r);
        var reason = Str(r, "reason") ?? "ENTRY_EXPIRED";
        return new(e.Seq, e.SimTimeUtc, "info", "Signal",
            string.IsNullOrEmpty(sym) ? "Order cancelled" : $"Order cancelled {sym}", reason);
    }

    private static NarrativeEvent BuildBar(JournalEntryEntity e)
    {
        var r = Root(e.EventJson);
        var sym = SymOf(r);
        var close = Dec(r, "close");
        return new(e.Seq, e.SimTimeUtc, "info", "System", $"Bar closed {sym}".Trim(), close.HasValue ? $"@{close.Value:F5}" : "");
    }

    private static NarrativeEvent BuildEquity(JournalEntryEntity e)
    {
        var r = Root(e.EventJson);
        var eq = Dec(r, "equity");
        var bal = Dec(r, "balance");
        return new(e.Seq, e.SimTimeUtc, "info", "System", "Equity snapshot",
            eq.HasValue ? $"{eq.Value:C2}" + (bal.HasValue ? $" (bal {bal.Value:C2})" : "") : "");
    }

    // ---- JSON helpers: case-insensitive; unwrap the {Value} envelope of Symbol/Price value objects ----

    private static JsonElement Root(string? json)
    {
        try { return JsonDocument.Parse(string.IsNullOrEmpty(json) ? "{}" : json).RootElement.Clone(); }
        catch { return JsonDocument.Parse("{}").RootElement.Clone(); }
    }

    private static bool TryProp(JsonElement e, string name, out JsonElement value)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static string? Str(JsonElement e, string name)
        => TryProp(e, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal? Dec(JsonElement e, string name)
        => TryProp(e, name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;

    /// <summary>Symbol serializes as {"Value":"EURUSD"}; tolerate a bare string too.</summary>
    private static string SymOf(JsonElement e)
    {
        if (!TryProp(e, "symbol", out var s)) return "";
        if (s.ValueKind == JsonValueKind.String) return s.GetString() ?? "";
        return Str(s, "value") ?? "";
    }

    /// <summary>Price (nullable) serializes as {"Value":1.2345} or null; tolerate a bare number too.</summary>
    private static decimal? PriceOf(JsonElement e, string name)
    {
        if (!TryProp(e, name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number) return p.GetDecimal();
        if (p.ValueKind == JsonValueKind.Object && TryProp(p, "value", out var v) && v.ValueKind == JsonValueKind.Number)
            return v.GetDecimal();
        return null;
    }
}

public sealed record NarrativeEvent(long Seq, DateTime SimTime, string Severity, string Category, string Headline, string Detail);
public sealed record NarrativeResponse(IReadOnlyList<NarrativeEvent> Events, long LatestSeq, bool HasMore);
