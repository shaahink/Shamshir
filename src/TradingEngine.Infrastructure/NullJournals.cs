namespace TradingEngine.Infrastructure;

/// <summary>
/// No-op <see cref="IDecisionJournal"/> / <see cref="IPipelineJournal"/> (iter-36 K5). The lossless
/// <c>StepRecord</c> journal is now the SINGLE journal writer (decisions, verdicts, effects + costs all
/// land there via the kernel loop), so the old <c>PipelineEventWriter</c>/<c>BarEvaluationHandler</c>
/// (Wait/DropOldest channels into PipelineEvents/BarEvaluations) are deleted. A handful of legacy
/// consumers still take these interfaces (ProtectionLedger's redundant line, the oracle TradingLoop,
/// the optional cTrader-adapter hook); they bind to these no-ops so nothing double-writes a second journal.
/// </summary>
public sealed class NullDecisionJournal : IDecisionJournal
{
    public void Record(DecisionRecord r) { }
}

public sealed class NullPipelineJournal : IPipelineJournal
{
    public void Write(string stage, string? correlationId, DateTime simTime, string detailJson = "{}") { }
}
