using System.Text.Json;

namespace TradingEngine.Domain;

public sealed record StrategyOverride(
    string? StrategyId = null,
    JsonElement? Parameters = null,
    PositionManagementOptions? PositionManagement = null,
    OrderEntryOptions? OrderEntry = null,
    string? RiskProfileId = null,
    RegimeFilterOptions? RegimeFilter = null,
    ReentryOptions? Reentry = null,
    bool? Enabled = null,
    string? Timeframe = null);

public sealed record SymbolTimeframePair(string Symbol, string Timeframe);

public sealed record EffectiveConfigEntry(
    string Id,
    string DisplayName,
    bool Enabled,
    IReadOnlyList<string> Symbols,
    string RiskProfileId,
    JsonElement Parameters,
    string Timeframe,
    PositionManagementOptions? PositionManagement = null,
    OrderEntryOptions? OrderEntry = null,
    RegimeFilterOptions? RegimeFilter = null,
    ReentryOptions? Reentry = null)
{
    public static EffectiveConfigEntry FromStrategyConfig(StrategyConfigEntry s) => new(
        s.Id,
        s.DisplayName,
        s.Enabled,
        s.Symbols,
        s.RiskProfileId,
        s.Parameters,
        s.Timeframe,
        s.PositionManagement,
        s.OrderEntry,
        s.RegimeFilter,
        s.Reentry);
}
