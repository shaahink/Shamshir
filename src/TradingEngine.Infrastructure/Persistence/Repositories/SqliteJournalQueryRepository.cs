using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Infrastructure.Persistence.Repositories;

public sealed class SqliteJournalQueryRepository(TradingDbContext db) : IJournalQueryRepository
{
    // Must mirror SqliteStepRecordSink.JsonOpts: the sink serializes enums (e.g. StrategyVerdict.Direction)
    // as STRINGS via JsonStringEnumConverter, so the reader needs the same converter or the default
    // number-mode enum reader throws "could not be converted" on the first fired-signal verdict.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public async Task<IReadOnlyList<StepRecord>> GetByRunAsync(
        string runId, long? afterSeq, int limit, CancellationToken ct, string? kind = null)
    {
        var query = db.JournalEntries.Where(e => e.RunId == runId);
        if (afterSeq.HasValue)
            query = query.Where(e => e.Seq > afterSeq.Value);
        // iter-38 B2/W-C2: push kind filter to the DB so paging works correctly (was applied post-limit).
        if (!string.IsNullOrEmpty(kind))
            query = query.Where(e => e.EventKind == kind);
        var entities = await query
            .OrderBy(e => e.Seq)
            .Take(limit)
            .ToListAsync(ct);
        return entities.Select(Map).ToList();
    }

    public async IAsyncEnumerable<StepRecord> StreamByRunAsync(
        string runId, long? afterSeq, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var query = db.JournalEntries.Where(e => e.RunId == runId);
        if (afterSeq.HasValue)
            query = query.Where(e => e.Seq > afterSeq.Value);
        await foreach (var entity in query.OrderBy(e => e.Seq).AsAsyncEnumerable().WithCancellation(ct))
            yield return Map(entity);
    }

    private static StepRecord Map(JournalEntryEntity e) => new(
        e.RunId,
        e.Seq,
        e.SimTimeUtc,
        e.EventKind,
        e.EventJson,
        DeserializeList<string>(e.EffectKinds),
        e.EffectsJson,
        Deserialize<RiskSnapshot>(e.RiskJson) ?? new RiskSnapshot(0, 0, 0, 0, 0, 0, 0, false, null, "Normal", 0),
        e.Regime,
        e.DecisionReason,
        DeserializeList<StrategyVerdict>(e.VerdictsJson));

    private static T? Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOpts);

    private static List<T> DeserializeList<T>(string json) =>
        JsonSerializer.Deserialize<List<T>>(json, JsonOpts) ?? [];
}
