using System.Reflection;
using FluentAssertions;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Tests.Architecture;

/// <summary>
/// iter-38 D5 / Stream T1: every persisted entity must carry audit timestamps. This pins the invariant so a
/// newly-added entity that forgets <see cref="IAuditableEntity"/> (and thus its CreatedAtUtc/UpdatedAtUtc
/// columns + auto-stamping) fails the gate instead of silently shipping un-audited rows.
/// </summary>
public sealed class EntityAuditableTests
{
    [Fact]
    public void All_persistence_entities_implement_IAuditableEntity()
    {
        var asm = typeof(IAuditableEntity).Assembly;
        const string ns = "TradingEngine.Infrastructure.Persistence.Entities";

        var entities = asm.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Namespace == ns)
            .ToList();

        entities.Should().NotBeEmpty("the entities namespace must contain mapped POCOs");

        var missing = entities
            .Where(t => !typeof(IAuditableEntity).IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToList();

        missing.Should().BeEmpty(
            "every persisted entity must implement IAuditableEntity (CreatedAtUtc/UpdatedAtUtc, iter-38 D5)");
    }
}
