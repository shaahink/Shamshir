using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Reconcile;

namespace TradingEngine.Web.Services;

public sealed class LedgerReconcileService
{
    private readonly TradingDbContext _db;
    private readonly IJournalQueryRepository _journal;

    // The journal serializes OrderProposed PascalCase with enums-as-strings (verified against the audit
    // DB); OccurredAtUtc is kept as a raw string so we normalize its (sometimes-'Z'-suffixed) value
    // ourselves rather than let STJ apply timezone conversion.
    private static readonly JsonSerializerOptions ProposalOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public LedgerReconcileService(TradingDbContext db, IJournalQueryRepository journal)
    {
        _db = db;
        _journal = journal;
    }

    /// <summary>The symbol a run traded — the parity gate needs it to size a "tick" for that instrument.</summary>
    public async Task<string> GetRunSymbolAsync(string runId, CancellationToken ct)
    {
        var symbol = await _db.BacktestRuns.AsNoTracking()
            .Where(r => r.RunId == runId)
            .Select(r => r.Symbol)
            .FirstOrDefaultAsync(ct);

        return string.IsNullOrWhiteSpace(symbol)
            ? throw new InvalidOperationException($"Run {runId} not found, or has no symbol.")
            : symbol;
    }

    public async Task<ReconcileLedger> BuildEngineLedgerAsync(string runId, CancellationToken ct)
    {
        var run = await _db.BacktestRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.RunId == runId, ct);

        if (run is null)
            throw new InvalidOperationException($"Run {runId} not found.");

        var trades = await _db.Trades.AsNoTracking()
            .Where(t => t.RunId == runId)
            .OrderBy(t => t.ClosedAtUtc)
            .Select(t => new ReconcileTrade(
                t.OpenedAtUtc,
                t.ClosedAtUtc,
                t.Direction,
                t.Lots,
                t.EntryPrice,
                t.ExitPrice,
                t.CommissionAmount,
                t.SwapAmount,
                t.NetPnLAmount,
                t.ExitReason))
            .ToListAsync(ct);

        return new ReconcileLedger(
            Source: $"engine:{runId}",
            NetProfit: run.NetProfit,
            GrossProfit: run.GrossPnL,
            Commission: run.CommissionTotal,
            Swap: run.SwapTotal,
            MaxDrawdownPct: (double)run.MaxDrawdownPct,
            TotalTrades: run.TotalTrades,
            WinningTrades: run.WinningTrades,
            WinRatePct: run.WinRatePct,
            Trades: trades);
    }

    /// <summary>
    /// P0.4 (F2): measures per-trade entry latency for a run — the delay between an order's PROPOSAL
    /// (journalled <c>OrderProposed.OccurredAtUtc</c>) and its FILL (<c>TradeResult.OpenedAtUtc</c>),
    /// joined on OrderId — plus a per-run distribution. Pure measurement; no execution change.
    /// </summary>
    public async Task<EntryLatencyReport> BuildEntryLatencyAsync(string runId, CancellationToken ct)
    {
        var proposals = new List<EntryLatencyProposal>();
        await foreach (var step in _journal.StreamByRunAsync(runId, afterSeq: null, ct))
        {
            if (step.EventKind != "OrderProposed" || string.IsNullOrEmpty(step.EventJson))
                continue;

            var dto = JsonSerializer.Deserialize<OrderProposedJson>(step.EventJson, ProposalOpts);
            if (dto is null || dto.OrderId == Guid.Empty || string.IsNullOrEmpty(dto.OccurredAtUtc))
                continue;
            if (!TryParseUtc(dto.OccurredAtUtc, out var proposedAtUtc))
                continue;

            proposals.Add(new EntryLatencyProposal(dto.OrderId, proposedAtUtc, dto.EntryTimeframe));
        }

        var fills = await _db.Trades.AsNoTracking()
            .Where(t => t.RunId == runId)
            .Select(t => new { t.OrderId, t.OpenedAtUtc })
            .ToListAsync(ct);

        return EntryLatencyAnalyzer.Analyze(
            proposals,
            fills.Select(f => new EntryLatencyFill(f.OrderId, f.OpenedAtUtc)));
    }

    // Both venues stamp UTC wall-clock; the cTrader path suffixes 'Z', the tape path does not. Treat a
    // missing offset as UTC and any present offset as UTC — never shift by the host's local zone.
    private static bool TryParseUtc(string value, out DateTime utc) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);

    private sealed record OrderProposedJson(Guid OrderId, string? OccurredAtUtc, Timeframe EntryTimeframe);
}
