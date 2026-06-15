namespace TradingEngine.Tests.Simulation.Harness;

public sealed class InMemoryDecisionJournal : IDecisionJournal
{
    private readonly List<DecisionRecord> _records = [];

    public IReadOnlyList<DecisionRecord> Records => _records;

    public void Record(DecisionRecord r) => _records.Add(r);
}
