using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Infrastructure.Persistence;

/// <summary>
/// iter-38 (owner decision D5 / Stream T2). Auto-stamps <see cref="IAuditableEntity"/> rows on save:
/// <c>CreatedAtUtc</c> on insert (when still default), <c>UpdatedAtUtc</c> always. Uses an injected clock so
/// tests are deterministic (<c>AuditStampInterceptorTests</c>).
///
/// TODO(iter-38 T2): register on the DbContext — in <c>EngineServiceCollectionExtensions.AddPersistence</c>'s
/// <c>AddDbContext</c> lambda add <c>o.AddInterceptors(new AuditStampInterceptor(clock))</c> (and the same for
/// the Web composition root if it builds its own options). Keep the manual <c>UpdatedAtUtc = DateTime.UtcNow</c>
/// in the stores or delete it — the interceptor makes it redundant.
/// </summary>
public sealed class AuditStampInterceptor(Func<DateTime> utcNow) : SaveChangesInterceptor
{
    public AuditStampInterceptor() : this(() => DateTime.UtcNow) { }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null) return;
        var now = utcNow();
        foreach (var entry in context.ChangeTracker.Entries<IAuditableEntity>())
        {
            if (entry.State == EntityState.Added && entry.Entity.CreatedAtUtc == default)
                entry.Entity.CreatedAtUtc = now;
            if (entry.State is EntityState.Added or EntityState.Modified)
                entry.Entity.UpdatedAtUtc = now;
        }
    }
}
