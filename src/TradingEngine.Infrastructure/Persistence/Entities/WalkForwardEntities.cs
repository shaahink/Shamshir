namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class WalkForwardJobEntity : IAuditableEntity
{
    public Guid Id { get; set; }
    public string SpecJson { get; set; } = "";
    public string Status { get; set; } = "pending";
    public int TotalWindows { get; set; }
    public int CompletedWindows { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? ErrorMessage { get; set; }

    public List<WalkForwardWindowResultEntity> Windows { get; set; } = [];
}

public sealed class WalkForwardWindowResultEntity : IAuditableEntity
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public int WindowIndex { get; set; }
    public DateTime TrainFromUtc { get; set; }
    public DateTime TrainToUtc { get; set; }
    public DateTime TestFromUtc { get; set; }
    public DateTime TestToUtc { get; set; }
    public string StrategyId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Timeframe { get; set; } = "";
    public string ChosenParamsJson { get; set; } = "";
    public string? TestRunId { get; set; }
    public decimal TestNetProfit { get; set; }
    public int TestTotalTrades { get; set; }
    public double TestWinRatePct { get; set; }
    public int TrialsCount { get; set; }
    public double? PlateauValue { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
