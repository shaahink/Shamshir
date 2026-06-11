namespace TradingEngine.Domain;

public interface IPipelineJournal
{
    void Write(string stage, string? correlationId, DateTime simTime, string detailJson = "{}");
}
