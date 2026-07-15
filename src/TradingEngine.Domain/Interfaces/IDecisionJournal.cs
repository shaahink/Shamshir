namespace TradingEngine.Domain;

public interface IDecisionJournal
{
    void Record(DecisionRecord r);
}
