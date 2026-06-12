using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Infrastructure.Persistence.Repositories;

public interface IExperimentRepository
{
    Task<ExperimentEntity> CreateAsync(ExperimentEntity experiment, CancellationToken ct);
    Task UpdateAsync(ExperimentEntity experiment, CancellationToken ct);
    Task<ExperimentEntity?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<ExperimentEntity>> GetAllAsync(CancellationToken ct);
    Task<ExperimentRunEntity> AddRunAsync(ExperimentRunEntity run, CancellationToken ct);
    Task<IReadOnlyList<ExperimentRunEntity>> GetRunsAsync(Guid experimentId, CancellationToken ct);
}
