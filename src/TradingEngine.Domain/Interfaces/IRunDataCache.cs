namespace TradingEngine.Domain;

public interface IRunDataCache
{
    void AppendJournal(string runId, IReadOnlyList<StepRecord> batch);
    void AppendEquity(string runId, IReadOnlyList<EquitySnapshot> batch);
    void AppendTrade(string runId, TradeResult trade);

    IReadOnlyList<StepRecord> GetJournal(string runId, int maxEntries = 10000);
    IReadOnlyList<EquitySnapshot> GetEquity(string runId);
    IReadOnlyList<TradeResult> GetTrades(string runId);

    void MarkCompleted(string runId);
    void Evict(string runId);
    bool HasRun(string runId);

    IReadOnlyList<string> GetRunIds();
    DateTime? GetCompletedAtUtc(string runId);
}
