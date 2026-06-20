using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Entities;
using TradingEngine.Infrastructure.Persistence.Repositories;

namespace TradingEngine.Tests.Integration.Journal;

/// <summary>
/// iter-37 Phase J (J4) — the journal read-path that the consolidated download + paged journal view use.
/// Seeds a temp SQLite DB with a known StepRecord stream and proves SQL paging by <c>Seq</c> is stable
/// (no overlap/gap) and the NDJSON export round-trips (one valid JSON object per line, in seq order).
/// </summary>
[Trait("Category", "Infrastructure")]
public sealed class JournalReadPathTests : IDisposable
{
    private const string Run = "run-jrnl";
    private const int N = 25;
    private readonly string _dbPath;

    public JournalReadPathTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"shamshir-jrnl-{Guid.NewGuid():N}.db");
        using var db = NewContext();
        db.Database.EnsureCreated();
        var risk = JsonSerializer.Serialize(new RiskSnapshot(10_000m, 10_000m, 0m, 0m, 0m, 0m, 0m, false, null, "Normal", 0));
        for (var i = 1; i <= N; i++)
        {
            db.JournalEntries.Add(new JournalEntryEntity
            {
                RunId = Run,
                Seq = i,
                SimTimeUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i),
                EventKind = "BarClosed",
                EventJson = "{}",
                EffectKinds = "[]",
                EffectsJson = "[]",
                RiskJson = risk,
                Regime = "Trending",
                VerdictsJson = "[]",
            });
        }
        db.SaveChanges();
    }

    private TradingDbContext NewContext() =>
        new(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public async Task Journal_Query_Paged_StableAcrossPages()
    {
        await using var db = NewContext();
        var repo = new SqliteJournalQueryRepository(db);
        const int pageSize = 10;

        var seen = new List<long>();
        long? after = null;
        while (true)
        {
            var page = await repo.GetByRunAsync(Run, after, pageSize, CancellationToken.None);
            if (page.Count == 0) break;
            page.Count.Should().BeLessThanOrEqualTo(pageSize);
            seen.AddRange(page.Select(r => r.Seq));
            after = page[^1].Seq;
        }

        seen.Should().Equal(Enumerable.Range(1, N).Select(i => (long)i),
            "paging by Seq yields every record once, in order, with no overlap or gap");
    }

    [Fact]
    public async Task Journal_Export_Ndjson_RoundTrips()
    {
        await using var db = NewContext();
        var repo = new SqliteJournalQueryRepository(db);
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        var sb = new StringBuilder();
        await foreach (var rec in repo.StreamByRunAsync(Run, null, CancellationToken.None))
            sb.Append(JsonSerializer.Serialize(rec, opts)).Append('\n');

        var lines = sb.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(N, "one JSON object per line");
        var seqs = lines.Select(l => JsonSerializer.Deserialize<StepRecord>(l, opts)!.Seq).ToList();
        seqs.Should().Equal(Enumerable.Range(1, N).Select(i => (long)i), "NDJSON round-trips in Seq order");
    }
}
