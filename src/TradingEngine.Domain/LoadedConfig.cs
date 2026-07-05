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
    string RiskProfileId,
    JsonElement Parameters)
{
    public string? Symbol { get; init; }
    public Timeframe? EntryTimeframe { get; init; }
    public RegimeFilterOptions? RegimeFilter { get; init; }
    public OrderEntryOptions? OrderEntry { get; init; }
    public PositionManagementOptions? PositionManagement { get; init; }
    public ReentryOptions? Reentry { get; init; }

    // P2.5: falsifiable-hypothesis metadata (thesis one-sentence claim + expected frequency/hold),
    // used by P4's frequency reality check.
    public string? Thesis { get; init; }
    public int? ExpectedTradesPerWeek { get; init; }
    public int? ExpectedHoldBars { get; init; }
}
