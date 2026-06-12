namespace TradingEngine.Domain;

public record ReentryOptions
{
    public bool BlockWhileSameDirectionOpen { get; init; } = true;
    public int CooldownBarsAfterSl { get; init; } = 5;
    public int CooldownBarsAfterTp { get; init; } = 2;
    public int CooldownBarsAfterEntry { get; init; } = 3;
}
