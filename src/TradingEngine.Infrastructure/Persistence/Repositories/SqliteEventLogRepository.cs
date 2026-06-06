using System.Text.Json;

namespace TradingEngine.Infrastructure.Persistence.Repositories;

public sealed class SqliteEventLogRepository(TradingDbContext db) : IEventLogRepository
{
    public async Task AppendAsync(EngineEvent evt, CancellationToken ct)
    {
        var entity = new EngineEventEntity
        {
            Id = Guid.NewGuid(),
            EventType = evt.GetType().Name,
            Payload = JsonSerializer.Serialize(evt, evt.GetType()),
            OccurredAtUtc = evt.OccurredAtUtc,
        };
        db.Events.Add(entity);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<EngineEvent>> GetRecentAsync(int count, CancellationToken ct)
    {
        var entities = await db.Events
            .OrderByDescending(e => e.OccurredAtUtc)
            .Take(count)
            .ToListAsync(ct);

        return entities.Select(e => DeserializeEvent(e)).ToList();
    }

    private static EngineEvent DeserializeEvent(EngineEventEntity entity)
    {
        return entity.EventType switch
        {
            nameof(TradeOpened) => JsonSerializer.Deserialize<TradeOpened>(entity.Payload)!,
            nameof(TradeClosed) => JsonSerializer.Deserialize<TradeClosed>(entity.Payload)!,
            nameof(TradeBlocked) => JsonSerializer.Deserialize<TradeBlocked>(entity.Payload)!,
            nameof(DrawdownBreached) => JsonSerializer.Deserialize<DrawdownBreached>(entity.Payload)!,
            nameof(ProtectionModeEntered) => JsonSerializer.Deserialize<ProtectionModeEntered>(entity.Payload)!,
            nameof(EquityUpdated) => JsonSerializer.Deserialize<EquityUpdated>(entity.Payload)!,
            _ => throw new NotSupportedException($"Unknown event type: {entity.EventType}"),
        };
    }
}
