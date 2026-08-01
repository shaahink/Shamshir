using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingEngine.Infrastructure.Persistence;

namespace TradingEngine.Tests.Integration.Support;

/// <summary>
/// A REAL in-memory SQLite database for integration tests — not the EF Core InMemory provider, which is
/// non-relational and diverges from production (no migrations/`EnsureCreated`, no FK/unique/PK constraint
/// enforcement, LINQ runs client-side so query semantics differ). This is the actual SQLite engine, so
/// the SQL translation, constraints (incl. the journal `(RunId,Seq)` PK) and schema match production.
///
/// Caveat handled here: a `:memory:` database lives only while its connection is open and EF opens/closes
/// a connection per operation — so we keep ONE connection open for the test's lifetime and reuse it for
/// every context/scope (via <see cref="Options"/> / <see cref="Connection"/>). The schema is created once.
/// Faster than a temp file and leaves nothing to clean up.
/// </summary>
public sealed class SqliteInMemory : IDisposable
{
    public SqliteConnection Connection { get; }
    public DbContextOptions<TradingDbContext> Options { get; }

    public SqliteInMemory()
    {
        Connection = new SqliteConnection("Data Source=:memory:");
        Connection.Open();
        Options = new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(Connection).Options;
        using var ctx = new TradingDbContext(Options);
        ctx.Database.EnsureCreated();
    }

    public TradingDbContext NewContext() => new(Options);

    public void Dispose() => Connection.Dispose();
}
