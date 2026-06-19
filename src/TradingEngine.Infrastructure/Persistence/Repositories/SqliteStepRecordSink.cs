using System.Text.Json;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Infrastructure.Persistence.Repositories;

public sealed class SqliteStepRecordSink(TradingDbContext db) : IStepRecordSink
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public async Task AppendBatchAsync(IReadOnlyList<StepRecord> batch, CancellationToken ct)
    {
        var entities = batch.Select(Map).ToList();
        db.JournalEntries.AddRange(entities);
        await db.SaveChangesAsync(ct);
    }

    private static JournalEntryEntity Map(StepRecord r) => new()
    {
        RunId = r.RunId,
        Seq = r.Seq,
        SimTimeUtc = r.SimTimeUtc,
        EventKind = r.EventKind,
        EventJson = r.EventJson,
        EffectKinds = JsonSerializer.Serialize(r.EffectKinds, JsonOpts),
        EffectsJson = r.EffectsJson,
        RiskJson = JsonSerializer.Serialize(r.Risk, JsonOpts),
        Regime = r.Regime,
        DecisionReason = r.DecisionReason,
        VerdictsJson = JsonSerializer.Serialize(r.StrategyVerdicts, JsonOpts),
    };
}
