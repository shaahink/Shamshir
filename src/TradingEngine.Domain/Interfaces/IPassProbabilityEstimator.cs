namespace TradingEngine.Domain;

public interface IPassProbabilityEstimator
{
    PassProbabilityEstimate Estimate(PassProbabilityInput input);
}
