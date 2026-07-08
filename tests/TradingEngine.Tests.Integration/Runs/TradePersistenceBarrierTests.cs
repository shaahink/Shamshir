using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Application;
using TradingEngine.Domain;
using TradingEngine.Infrastructure;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Entities;
using TradingEngine.Infrastructure.Persistence.Repositories;
using TradingEngine.Tests.Integration.Support;

namespace TradingEngine.Tests.Integration.Runs;

// P0.3 (F6) — the trade-persistence integrity barrier, through the REAL persistence path (SQLite).
// The audited BTC cTrader run f7b0538d journalled 12 proposals + 17 fills + 7 closes yet persisted 0
// TradeResults and reported TotalTrades=0 — the close→TradeResult path lost everything before the run
// finalized. The barrier reconciles journalled PublishTradeClosed effects vs TradeResults rows and
// backfills the lost trades from the journal (via the SAME TradeResultFactory the live path uses), so
// the run reports the real count with a TRADES_LOST warning, never a silent zero.
[Trait("Category", "Infrastructure")]
public sealed class TradePersistenceBarrierTests : IDisposable
{
    private const string Run = "run-btc";
    private readonly SqliteInMemory _db = new();
    private readonly ServiceProvider _sp;

    // Production serializes EffectsJson PascalCase with enums-as-strings (verified against the audit DB);
    // the barrier's reader must round-trip that exact shape.
    private static readonly JsonSerializerOptions EffectOpts = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public TradePersistenceBarrierTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TradingDbContext>(o => o.UseSqlite(_db.Connection));
        _sp = services.BuildServiceProvider();
    }

    private ISymbolInfoRegistry BtcRegistry()
    {
        var reg = new SymbolInfoRegistry();
        // BTCUSD: pip size 0.01, contract 1, quote == account currency (USD) so no cross-rate needed.
        reg.Register(new SymbolInfo(Symbol.Parse("BTCUSD"), SymbolCategory.Crypto, "BTC", "USD",
            0.01m, 0.01m, 1m, 0.01m, 100m, 0.01m, 1m, 0.01m));
        return reg;
    }

    private void SeedClose(TradingDbContext db, long seq, Guid positionId, decimal entry, decimal exit,
        string reason, decimal grossPnL)
    {
        var close = new PublishTradeClosed(
            positionId, Symbol.Parse("BTCUSD"), TradeDirection.Long, 1.0m,
            new Price(entry), new Price(exit), new Price(entry - 500m), new Price(exit),
            "trend-breakout", reason,
            new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc).AddHours(seq),
            new DateTime(2026, 6, 19, 0, 0, 0, DateTimeKind.Utc),
            OrderId: positionId, RiskProfileId: "standard", OrderEntryMethod: "Market",
            GrossProfit: grossPnL, NetProfit: grossPnL - 30m, Commission: 30m, Swap: 0m,
            InitialStopLoss: new Price(entry - 500m));

        // Parallel arrays exactly like the kernel/sink write: EffectKinds[i] names EffectsJson[i].
        var kinds = new[] { "RecordDecisionEvent", "DeregisterRisk", "PublishTradeClosed" };
        var effects = new object[]
        {
            new { Reason = reason },
            new { PositionId = positionId },
            close,
        };
        db.JournalEntries.Add(new JournalEntryEntity
        {
            RunId = Run,
            Seq = seq,
            SimTimeUtc = close.ClosedAtUtc,
            EventKind = "OrderFilled",
            EffectKinds = JsonSerializer.Serialize(kinds, EffectOpts),
            EffectsJson = JsonSerializer.Serialize(effects, EffectOpts),
        });
    }

    private TradePersistenceBarrier NewBarrier(TradingDbContext db)
    {
        var reg = BtcRegistry();
        var crossRate = new CrossRateStore();
        return new TradePersistenceBarrier(
            new SqliteJournalQueryRepository(db),
            new SqliteTradeRepository(db),
            db,
            reg,
            crossRate.Convert,
            NullLogger<TradePersistenceBarrier>.Instance);
    }

    [Fact]
    public async Task BtcScenario_JournalHasCloses_TradeResultsEmpty_BackfillsAll()
    {
        // The exact F6 shape: 3 closes journalled, ZERO persisted (the venue was killed before the
        // close→TradeResult path ran).
        using (var db = _db.NewContext())
        {
            SeedClose(db, 1, Guid.Parse("00000000-0000-0000-0000-000000000001"), 60000m, 60800m, "TP", 800m);
            SeedClose(db, 2, Guid.Parse("00000000-0000-0000-0000-000000000002"), 60800m, 60300m, "SL", -500m);
            SeedClose(db, 3, Guid.Parse("00000000-0000-0000-0000-000000000003"), 60300m, 61200m, "TP", 900m);
            await db.SaveChangesAsync();
        }

        // BEFORE: journal has 3 closes, TradeResults has 0 (the audited silent loss).
        using (var db = _db.NewContext())
        {
            (await db.Trades.CountAsync(t => t.RunId == Run)).Should().Be(0, "the F6 repro: trades vanished");
        }

        TradePersistenceReconciliation recon;
        using (var db = _db.NewContext())
        {
            recon = await NewBarrier(db).ReconcileAndBackfillAsync(Run, CancellationToken.None);
        }

        recon.Expected.Should().Be(3);
        recon.Persisted.Should().Be(0);
        recon.Backfilled.Should().Be(3);
        recon.HasLoss.Should().BeTrue("a shortfall must surface as a TRADES_LOST warning");

        // AFTER: all 3 trades restored, reconstructed faithfully from the journal.
        using (var db = _db.NewContext())
        {
            var trades = await db.Trades.Where(t => t.RunId == Run).OrderBy(t => t.ClosedAtUtc).ToListAsync();
            trades.Should().HaveCount(3, "backfill restored every lost trade — NOT TotalTrades=0");
            trades[0].Symbol.Should().Be("BTCUSD");
            trades[0].ExitReason.Should().Be("TP");
            trades[0].NetPnLAmount.Should().Be(770m, "gross 800 − 30 commission (venue-authoritative)");
            trades[1].ExitReason.Should().Be("SL");
            trades[1].NetPnLAmount.Should().Be(-530m);
        }
    }

    [Fact]
    public async Task PartialPersistence_BackfillsOnlyTheMissing_NoDuplicates()
    {
        var p1 = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
        var p2 = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
        using (var db = _db.NewContext())
        {
            SeedClose(db, 1, p1, 60000m, 60800m, "TP", 800m);
            SeedClose(db, 2, p2, 60800m, 60300m, "SL", -500m);
            await db.SaveChangesAsync();
        }

        // Persist ONLY the first close (as the live path would have, before the loss).
        using (var db = _db.NewContext())
        {
            var reg = BtcRegistry();
            var repo = new SqliteTradeRepository(db);
            var existing = new TradeResult(Guid.NewGuid(), p1, Symbol.Parse("BTCUSD"), TradeDirection.Long,
                1.0m, new Price(60000m), new Price(60800m), new Price(59500m), new Price(60800m),
                new DateTime(2026, 6, 19, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 6, 20, 1, 0, 0, DateTimeKind.Utc),
                new Money(800m, "USD"), new Money(30m, "USD"), new Money(0m, "USD"), new Money(770m, "USD"),
                new Pips(0), 0, new Pips(0), new Pips(0), "TP", "trend-breakout", "standard",
                InitialStopLoss: new Price(59500m));
            await repo.SaveAsync(existing, Run, CancellationToken.None);
        }

        TradePersistenceReconciliation recon;
        using (var db = _db.NewContext())
        {
            recon = await NewBarrier(db).ReconcileAndBackfillAsync(Run, CancellationToken.None);
        }

        recon.Expected.Should().Be(2);
        recon.Persisted.Should().Be(1);
        recon.Backfilled.Should().Be(1, "only the missing SL close is backfilled");

        using (var db = _db.NewContext())
        {
            var trades = await db.Trades.Where(t => t.RunId == Run).ToListAsync();
            trades.Should().HaveCount(2, "no duplicate of the already-persisted TP close");
            trades.Count(t => t.ExitReason == "TP").Should().Be(1);
            trades.Count(t => t.ExitReason == "SL").Should().Be(1);
        }
    }

    [Fact]
    public async Task FullyPersisted_NoBackfill_NoLoss()
    {
        var p1 = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
        using (var db = _db.NewContext())
        {
            SeedClose(db, 1, p1, 60000m, 60800m, "TP", 800m);
            await db.SaveChangesAsync();
        }
        using (var db = _db.NewContext())
        {
            await NewBarrier(db).ReconcileAndBackfillAsync(Run, CancellationToken.None);
        }

        TradePersistenceReconciliation recon;
        using (var db = _db.NewContext())
        {
            recon = await NewBarrier(db).ReconcileAndBackfillAsync(Run, CancellationToken.None);
        }

        recon.Expected.Should().Be(1);
        recon.Persisted.Should().Be(1);
        recon.Backfilled.Should().Be(0);
        recon.HasLoss.Should().BeFalse("everything already persisted ⇒ clean completed, no warning");
    }

    public void Dispose()
    {
        _sp.Dispose();
        _db.Dispose();
    }
}
