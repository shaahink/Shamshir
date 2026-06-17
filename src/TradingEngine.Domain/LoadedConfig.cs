using System.Text.Json;

namespace TradingEngine.Domain;

public sealed record LoadedConfig(
    IReadOnlyList<PropFirmRuleSet> PropFirms,
    IReadOnlyList<RiskProfile> RiskProfiles)
{
    public IReadOnlyList<StrategyConfigEntry> StrategyConfigs { get; set; } = [];
    public IReadOnlyList<NewsBlockWindow> NewsWindows { get; init; } = [];
    public StrategyRotationOptions? StrategyRotation { get; init; }
    public GovernorOptions Governor { get; init; } = new();
    public SizingPolicyOptions SizingPolicy { get; init; } = new();
    public RegimeOptions Regime { get; init; } = new();
}

public sealed record StrategyConfigEntry(
    string Id,
    string DisplayName,
    bool Enabled,
    IReadOnlyList<string> Symbols,
    string RiskProfileId,
    JsonElement Parameters,
    string Timeframe = "H1")
{
    public RegimeFilterOptions? RegimeFilter { get; init; }
    public OrderEntryOptions? OrderEntry { get; init; }
    public PositionManagementOptions? PositionManagement { get; init; }
    public ReentryOptions? Reentry { get; init; }
}
