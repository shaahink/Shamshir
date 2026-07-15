using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence.Repositories;
using TradingEngine.Tests.Integration.Support;

namespace TradingEngine.Tests.Integration.Journal;

/// <summary>
/// Regression guard for the write/read enum-serialization mismatch that crashed GET /runs/{id}/bar-decisions
/// (and every RunProjection/StrategyBreakdown query) with:
///   "The JSON value could not be converted to StrategyVerdict. Path: $[0].direction".
///
/// <see cref="SqliteStepRecordSink"/> serializes enums (StrategyVerdict.Direction) as STRINGS via
/// JsonStringEnumConverter. <see cref="SqliteJournalQueryRepository"/> historically read without that
/// converter, so the default number-mode enum reader threw on the first verdict that carried a Direction
/// (i.e. the first bar where a signal actually fired). Latent since iter-35 because every prior test seeded
/// Direction=null. This round-trips a NON-NULL Direction through the real sink + real reader.
/// </summary>
[Trait("Category", "Infrastructure")]
public sealed class VerdictDirectionRoundTripTests : IDisposable
{
    private const string Run = "run-dir";
    private readonly SqliteInMemory _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task FiredSignalVerdict_WithDirection_RoundTripsThroughSinkAndReader()
    {
        // Write via the REAL production sink (serializes Direction as the string "Long").
        await using (var writeCtx = _db.NewContext())
        {
            var sink = new SqliteStepRecordSink(writeCtx);
            var record = new StepRecord(
                Run, Seq: 1, SimTimeUtc: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                EventKind: "BarClosed", EventJson: "{}", EffectKinds: [], EffectsJson: "[]",
                Risk: new RiskSnapshot(0, 0, 0, 0, 0, 0, 0, false, null, "Normal", 0),
                Regime: null, DecisionReason: null,
                StrategyVerdicts: [new StrategyVerdict(
                    "A", HadEnoughBars: true, SignalFired: true,
                    Direction: TradeDirection.Long, Reason: "OK", Indicators: null)]);
            await sink.AppendBatchAsync([record], CancellationToken.None);
        }

        // Read via the REAL production reader — this used to throw JsonException on the string enum.
        await using var readCtx = _db.NewContext();
        var repo = new SqliteJournalQueryRepository(readCtx);
        var rows = await repo.GetByRunAsync(Run, afterSeq: null, limit: 50, CancellationToken.None);

        var verdict = rows.Should().ContainSingle().Which.StrategyVerdicts.Should().ContainSingle().Subject;
        verdict.Direction.Should().Be(TradeDirection.Long);
        verdict.SignalFired.Should().BeTrue();
    }
}
