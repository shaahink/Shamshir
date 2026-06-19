namespace TradingEngine.Domain;

public interface IDatasetRepository
{
    Task SaveAsync(DatasetRef dataset, CancellationToken ct);
    Task<DatasetRef?> GetByIdAsync(string datasetId, CancellationToken ct);
    Task<IReadOnlyList<DatasetRef>> GetAllAsync(CancellationToken ct);
    Task<string?> GetContentHashAsync(IReadOnlyList<string> symbols, IReadOnlyList<string> timeframes, DateTime fromUtc, DateTime toUtc, DatasetGranularity granularity, CancellationToken ct);
}
