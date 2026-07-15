using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Infrastructure.Persistence;

/// <summary>
/// iter-38 (owner decision D5 / Stream T2). Auto-stamps <see cref="IAuditableEntity"/> rows on save:
/// <c>CreatedAtUtc</c> on insert (when still default), <c>UpdatedAtUtc</c> always. Uses an injected
/// <see cref="IEngineClock"/> for test determinism; falls back to <c>DateTime.UtcNow</c> when the
/// parameterless constructor is used.
/// </summary>
public sealed class AuditStampInterceptor : SaveChangesInterceptor
{
    private readonly Func<DateTime> _utcNow;

    public AuditStampInterceptor() : this(() => DateTime.UtcNow) { }

    public AuditStampInterceptor(IEngineClock clock) : this(() => clock.UtcNow) { }

    public AuditStampInterceptor(Func<DateTime> utcNow)
    {
        _utcNow = utcNow;
    }

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
        var now = _utcNow();
        foreach (var entry in context.ChangeTracker.Entries<IAuditableEntity>())
        {
            if (entry.State == EntityState.Added && entry.Entity.CreatedAtUtc == default)
                entry.Entity.CreatedAtUtc = now;
            if (entry.State is EntityState.Added or EntityState.Modified)
                entry.Entity.UpdatedAtUtc = now;
        }
    }
}
