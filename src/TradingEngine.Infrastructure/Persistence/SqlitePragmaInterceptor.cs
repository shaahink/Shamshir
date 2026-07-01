using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;

namespace TradingEngine.Infrastructure.Persistence;

public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        base.ConnectionOpened(connection, eventData);
        ApplyPragmas(connection);
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection,
        ConnectionEndEventData eventData, CancellationToken ct = default)
    {
        await base.ConnectionOpenedAsync(connection, eventData, ct);
        ApplyPragmas(connection);
    }

    private static void ApplyPragmas(DbConnection connection)
    {
        if (connection is not SqliteConnection sqlite) return;

        using var cmd = sqlite.CreateCommand();
        cmd.CommandText = """
            PRAGMA cache_size=-65536;
            PRAGMA synchronous=NORMAL;
            PRAGMA temp_store=MEMORY;
            PRAGMA mmap_size=268435456;
            PRAGMA busy_timeout=5000;
            """;
        cmd.ExecuteNonQuery();
    }
}
