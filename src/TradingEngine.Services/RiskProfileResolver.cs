namespace TradingEngine.Services;

public sealed class RiskProfileResolver(IReadOnlyList<RiskProfile> riskProfiles) : IRiskProfileResolver
{
    public RiskProfile Resolve(string riskProfileId)
    {
        var profile = riskProfiles.FirstOrDefault(p => p.Id == riskProfileId);
        if (profile is null)
            throw new InvalidOperationException($"Unknown risk profile: {riskProfileId}");
        return profile;
    }
}
