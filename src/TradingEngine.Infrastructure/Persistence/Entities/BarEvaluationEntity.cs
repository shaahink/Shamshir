namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class BarEvaluationEntity
{
    public Guid Id { get; set; }
    public string RunId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Timeframe { get; set; } = "";
    public DateTime BarOpenTimeUtc { get; set; }
    public string StrategyId { get; set; } = "";
    public string IndicatorValuesJson { get; set; } = "{}";
    public bool SignalFired { get; set; }
    public string? SignalDirection { get; set; }
    public string Reason { get; set; } = "";
    public DateTime OccurredAtUtc { get; set; }
}
