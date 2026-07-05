namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class ExitCalibrationEntity
{
    public Guid Id { get; set; }
    public string StrategyId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string EntryTimeframe { get; set; } = "";
    public string? Regime { get; set; }
    public double SlAtrMultiple { get; set; } = 4.0;
    public double? TpRrMultiple { get; set; }
    public double? BeTriggerR { get; set; }
    public double? BeOffsetPips { get; set; }
    public double? TrailAtrMultiple { get; set; }
    public double? PartialTriggerR { get; set; }
    public double? PartialCloseFraction { get; set; }
    public string DatasetId { get; set; } = "";
    public DateTime IsStartUtc { get; set; }
    public DateTime IsEndUtc { get; set; }
    public DateTime? OosStartUtc { get; set; }
    public DateTime? OosEndUtc { get; set; }
    public DateTime FittedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class ReferenceScaleEntity
{
    public Guid Id { get; set; }
    public string Symbol { get; set; } = "";
    public string EntryTimeframe { get; set; } = "";
    public double MedianAtrPips { get; set; }
    public double MedianBarRangePips { get; set; }
    public double MedianSpreadPips { get; set; }
    public int SampleBarCount { get; set; }
    public DateTime RefreshedAtUtc { get; set; }
}
