namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class ExperimentRunEntity
{
    public Guid Id { get; set; }
    public Guid ExperimentId { get; set; }
    public ExperimentEntity Experiment { get; set; } = null!;
    public string BacktestRunId { get; set; } = "";
    public string VariantLabel { get; set; } = "";
    public int FoldIndex { get; set; }
    public string FoldRole { get; set; } = "Test";
    public string ScoreJson { get; set; } = "{}";
}
