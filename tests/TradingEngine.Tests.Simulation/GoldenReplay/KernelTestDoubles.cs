using TradingEngine.Engine;

namespace TradingEngine.Tests.Simulation.GoldenReplay;

/// <summary>
/// A test effect executor that captures kernel effects for assertion, rather than executing them
/// against a real venue. Used by KernelAcceptanceTests to verify the kernel produces the same
/// SubmitOrder/RecordDecision effects as the old gate.
/// </summary>
public sealed class CaptureEffectExecutor : IEffectExecutor
{
    private readonly List<EngineEffect> _effects = [];

    public IReadOnlyList<EngineEffect> Effects => _effects;

    public Task ExecuteAsync(EngineEffect effect, CancellationToken ct)
    {
        _effects.Add(effect);
        return Task.CompletedTask;
    }
}

/// <summary>
/// In-memory step record sink for the kernel acceptance test. Captures journal entries.
/// </summary>
public sealed class InMemoryStepRecordSink : IStepRecordSink
{
    private readonly List<StepRecord> _records = [];

    public IReadOnlyList<StepRecord> Records => _records;

    public Task AppendBatchAsync(IReadOnlyList<StepRecord> batch, CancellationToken ct)
    {
        _records.AddRange(batch);
        return Task.CompletedTask;
    }
}
