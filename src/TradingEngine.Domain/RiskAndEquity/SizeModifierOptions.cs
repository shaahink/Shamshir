namespace TradingEngine.Domain;

public record SizeModifierOptions
{
    public double MinCombinedScale { get; init; } = 0.1;
    public double MaxCombinedScale { get; init; } = 1.5;
    public AtrScalingOptions AtrRegime { get; init; } = new();
    public TimeOfDayScalingOptions TimeOfDay { get; init; } = new();
    public ConfidenceScalingOptions Confidence { get; init; } = new();
}

public record AtrScalingOptions
{
    public bool Enabled { get; init; }
    public int AtrPeriod { get; init; } = 14;
    public int AtrBaselinePeriod { get; init; } = 100;
    public double HighAtrMultiple { get; init; } = 1.5;
    public double LowAtrMultiple { get; init; } = 0.5;
    public double HighAtrSizeScale { get; init; } = 0.7;
    public double LowAtrSizeScale { get; init; } = 1.2;
    public double ExtremeAtrSizeScale { get; init; } = 0.3;
}

public record TimeOfDayScalingOptions
{
    public bool Enabled { get; init; }
    public IReadOnlyList<TimeOfDayScaleWindow> Windows { get; init; } = [];
}

public record TimeOfDayScaleWindow(TimeSpan StartUtc, TimeSpan EndUtc, double Scale);

public record ConfidenceScalingOptions
{
    public bool Enabled { get; init; }
    public int LossStreakThreshold { get; init; } = 3;
    public double LossStreakScale { get; init; } = 0.5;
    public int WinStreakThreshold { get; init; } = 5;
    public double WinStreakScale { get; init; } = 1.2;
}
