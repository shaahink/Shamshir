using TradingEngine.Domain.Experiments;

namespace TradingEngine.Application.Experiments;

public interface IExperimentRunner
{
    Task<ExperimentResult> RunAsync(ExperimentSpec spec, CancellationToken ct);
}

public record ExperimentResult(
    Guid ExperimentId,
    string Name,
    bool Success,
    string? ErrorMessage,
    IReadOnlyList<VariantScore> VariantScores);
