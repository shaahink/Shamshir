using System.Text.Json;
using System.Text.Json.Serialization;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Infrastructure.Persistence.Repositories;

public sealed class SqliteStepRecordSink(TradingDbContext db, IRunDataCache? runDataCache = null) : IStepRecordSink
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions EventJsonOpts = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task AppendBatchAsync(IReadOnlyList<StepRecord> batch, CancellationToken ct)
    {
        var entities = batch.Select(Map).ToList();
        db.JournalEntries.AddRange(entities);
        await db.SaveChangesAsync(ct);
        if (runDataCache is not null && batch.Count > 0 && !string.IsNullOrEmpty(batch[0].RunId))
            runDataCache.AppendJournal(batch[0].RunId, batch);
    }

    private static JournalEntryEntity Map(StepRecord r)
    {
        // F3: serialize event/effects in the background sink, not on the pump thread.
        // If RawEvent/RawEffects are present (kernel path), serialize them here.
        // Fall back to pre-serialized EventJson/EffectsJson for legacy callers.
        var eventJson = r.EventJson;
        if (string.IsNullOrEmpty(eventJson) && r.RawEvent is not null)
            eventJson = JsonSerializer.Serialize(r.RawEvent, r.RawEvent.GetType(), EventJsonOpts);

        var effectsJson = r.EffectsJson;
        if (string.IsNullOrEmpty(effectsJson) && r.RawEffects is not null)
            effectsJson = JsonSerializer.Serialize(r.RawEffects, EventJsonOpts);

        return new JournalEntryEntity
        {
            RunId = r.RunId,
            Seq = r.Seq,
            SimTimeUtc = r.SimTimeUtc,
            EventKind = r.EventKind,
            EventJson = eventJson,
            EffectKinds = JsonSerializer.Serialize(r.EffectKinds, JsonOpts),
            EffectsJson = effectsJson,
            RiskJson = JsonSerializer.Serialize(r.Risk, JsonOpts),
            Regime = r.Regime,
            DecisionReason = r.DecisionReason,
            VerdictsJson = JsonSerializer.Serialize(r.StrategyVerdicts, JsonOpts),
        };
    }
}
