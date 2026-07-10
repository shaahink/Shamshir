using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence.Entities;
using TradingEngine.Infrastructure.Persistence.Repositories;
using TradingEngine.Infrastructure.Reconcile;
using TradingEngine.Tests.Integration.Support;
using TradingEngine.Web.Services;

namespace TradingEngine.Tests.Integration.Runs;

// P0.4 (F2) — entry-latency instrumentation through the REAL read path (SQLite journal + TradeResults).
// Reproduces the audited March EURUSD H1 pair credential-free: the tape leg proposes at the H1 bar-open
// and fills at the next M1 open after the H1 close (06:00→07:01 = 3660s); the cTrader leg proposes at
// the same instant but fills one full H1 decision bar later (06:00→08:00 = 7200s). The audit DB stamps
// cTrader OccurredAtUtc with a trailing 'Z' and tape without — both must yield the true delta.
[Trait("Category", "Infrastructure")]
public sealed class EntryLatencyReconcileTests : IDisposable
{
    private readonly SqliteInMemory _db = new();

    // Mirror the production journal sink: PascalCase, enums-as-strings.
    private static readonly JsonSerializerOptions SinkOpts = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static Guid Oid(int n) => Guid.Parse($"{n:D8}-0000-0000-0000-000000000000");

    private void SeedProposal(TradingDbContext db, string runId, long seq, int orderId, DateTime proposedAt)
    {
        var proposed = new OrderProposed(
            Oid(orderId), Symbol.Parse("EURUSD"), TradeDirection.Short, OrderType.Market,
            LimitPrice: null, StopLoss: new Price(1.16329m), TakeProfit: null,
            StrategyId: "trend-breakout", SignalPriceMid: 1.16069m, SlPips: 26m, PipValuePerLot: 10m,
            OccurredAtUtc: proposedAt, EntryTimeframe: Timeframe.H1);

        db.JournalEntries.Add(new JournalEntryEntity
        {
            RunId = runId,
            Seq = seq,
            SimTimeUtc = proposedAt,
            EventKind = "OrderProposed",
            EventJson = JsonSerializer.Serialize(proposed, SinkOpts),
            EffectKinds = "[]",
            EffectsJson = "[]",
        });
    }

    private void SeedFill(TradingDbContext db, string runId, int orderId, DateTime openedAt)
    {
        db.Trades.Add(new TradeResultEntity
        {
            Id = Guid.NewGuid(),
            PositionId = Oid(orderId),
            OrderId = Oid(orderId),
            RunId = runId,
            Symbol = "EURUSD",
            Direction = "Short",
            EntryTimeframe = "H1",
            OpenedAtUtc = openedAt,
            ClosedAtUtc = openedAt.AddHours(1),
        });
    }

    private async Task<EntryLatencyReport> LatencyAsync(string runId)
    {
        using var db = _db.NewContext();
        var svc = new LedgerReconcileService(db, new SqliteJournalQueryRepository(db));
        return await svc.BuildEntryLatencyAsync(runId, CancellationToken.None);
    }

    [Fact]
    public async Task March_pair_reproduces_the_audited_f2_lag()
    {
        // Tape: proposals at bar-open (Kind=Unspecified ⇒ serialized WITHOUT 'Z', like the audit DB),
        // fills at next-M1-open after the H1 close. Plus 3 late proposals that never filled (F3).
        var tapeP1 = new DateTime(2026, 3, 5, 6, 0, 0, DateTimeKind.Unspecified);
        var tapeP2 = new DateTime(2026, 3, 5, 15, 0, 0, DateTimeKind.Unspecified);
        var tapeP3 = new DateTime(2026, 3, 6, 12, 0, 0, DateTimeKind.Unspecified);

        // cTrader: SAME instants but Kind=Utc ⇒ serialized WITH a trailing 'Z' (the audit-DB quirk).
        var ctP1 = new DateTime(2026, 3, 5, 6, 0, 0, DateTimeKind.Utc);
        var ctP2 = new DateTime(2026, 3, 5, 15, 0, 0, DateTimeKind.Utc);
        var ctP3 = new DateTime(2026, 3, 6, 12, 0, 0, DateTimeKind.Utc);

        using (var db = _db.NewContext())
        {
            SeedProposal(db, "tape-mar", 1, 1, tapeP1);
            SeedProposal(db, "tape-mar", 2, 2, tapeP2);
            SeedProposal(db, "tape-mar", 3, 3, tapeP3);
            SeedProposal(db, "tape-mar", 4, 4, new DateTime(2026, 3, 8, 21, 0, 0, DateTimeKind.Unspecified));
            SeedProposal(db, "tape-mar", 5, 5, new DateTime(2026, 3, 9, 1, 0, 0, DateTimeKind.Unspecified));
            SeedFill(db, "tape-mar", 1, new DateTime(2026, 3, 5, 7, 1, 0, DateTimeKind.Unspecified));
            SeedFill(db, "tape-mar", 2, new DateTime(2026, 3, 5, 16, 1, 0, DateTimeKind.Unspecified));
            SeedFill(db, "tape-mar", 3, new DateTime(2026, 3, 6, 13, 1, 0, DateTimeKind.Unspecified));

            SeedProposal(db, "ct-mar", 1, 1, ctP1);
            SeedProposal(db, "ct-mar", 2, 2, ctP2);
            SeedProposal(db, "ct-mar", 3, 3, ctP3);
            SeedFill(db, "ct-mar", 1, new DateTime(2026, 3, 5, 8, 0, 0, DateTimeKind.Unspecified));
            SeedFill(db, "ct-mar", 2, new DateTime(2026, 3, 5, 17, 0, 0, DateTimeKind.Unspecified));
            SeedFill(db, "ct-mar", 3, new DateTime(2026, 3, 6, 14, 0, 0, DateTimeKind.Unspecified));
            await db.SaveChangesAsync();
        }

        var tape = await LatencyAsync("tape-mar");
        var ct = await LatencyAsync("ct-mar");

        // Tape: 3 filled trades (the 2 late proposals dropped — F3), each 3660s = 1 H1 bar + 1 M1 bar.
        tape.MatchedTrades.Should().Be(3);
        tape.UnmatchedFills.Should().Be(0);
        tape.DelaySeconds.Median.Should().Be(3660);
        tape.DelaySeconds.Min.Should().Be(3660);
        tape.DelaySeconds.Max.Should().Be(3660);
        tape.DelayBars.Median.Should().BeApproximately(3660d / 3600d, 1e-9);

        // cTrader: each 7200s = 2 H1 bars → one full decision bar later than tape (F2), Z-suffix and all.
        ct.MatchedTrades.Should().Be(3);
        ct.DelaySeconds.Median.Should().Be(7200);
        ct.DelayBars.Median.Should().BeApproximately(2.0, 1e-9);

        // The headline F2 number: the venue entry-latency gap ≈ one H1 decision bar.
        (ct.DelaySeconds.Median - tape.DelaySeconds.Median).Should().Be(3540);
    }

    public void Dispose() => _db.Dispose();
}
