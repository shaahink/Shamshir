namespace TradingEngine.Domain;

public record PositionManagementOptions
{
    public SlOptions StopLoss { get; init; } = new();
    public TpOptions TakeProfit { get; init; } = new();
    public BreakevenOptions Breakeven { get; init; } = new();
    public TrailingOptions Trailing { get; init; } = new();
}

public record SlOptions
{
    public string Method { get; init; } = "AtrMultiple";
    public double AtrMultiple { get; init; } = 1.5;
    public double FixedPips { get; init; }
    public double MaxPips { get; init; } = 100;
}

public record TpOptions
{
    public string Method { get; init; } = "RrMultiple";
    public double RrMultiple { get; init; } = 2.0;
    public double FixedPips { get; init; }
    public double AtrMultiple { get; init; }
}

public record BreakevenOptions
{
    public bool Enabled { get; init; }
    public double TriggerRMultiple { get; init; } = 1.0;
    public double OffsetPips { get; init; } = 1.0;
}

public record TrailingOptions
{
    public string Method { get; init; } = "None";
    public double StepPips { get; init; } = 10;
    public double AtrMultiple { get; init; } = 1.0;
    public bool ActivateAfterBreakeven { get; init; } = true;
}
