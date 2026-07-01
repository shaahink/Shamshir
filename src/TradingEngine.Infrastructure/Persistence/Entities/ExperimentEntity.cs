namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class ExperimentEntity : IAuditableEntity
{
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Hypothesis { get; set; } = "";
    public string SpecJson { get; set; } = "{}";
    public string Status { get; set; } = "Pending";
    public DateTime CreatedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public ICollection<ExperimentRunEntity> Runs { get; set; } = [];
}
