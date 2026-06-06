namespace TradingEngine.Domain;

public interface IRiskProfileResolver
{
    RiskProfile Resolve(string riskProfileId);
}
