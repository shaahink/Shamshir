using System.Text.Json;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Infrastructure.Persistence.Repositories;

public sealed class SqliteDatasetRepository(TradingDbContext db) : IDatasetRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task SaveAsync(DatasetRef dataset, CancellationToken ct)
    {
        var entity = new DatasetEntity
        {
            Id = dataset.DatasetId,
            ContentHash = dataset.ContentHash,
            Symbols = JsonSerializer.Serialize(dataset.Symbols, JsonOptions),
            Timeframes = JsonSerializer.Serialize(dataset.Timeframes, JsonOptions),
            FromUtc = dataset.FromUtc,
            ToUtc = dataset.ToUtc,
            Granularity = dataset.Granularity.ToString(),
            RowCount = dataset.RowCount,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.Datasets.Add(entity);
        await db.SaveChangesAsync(ct);
    }

    public async Task<DatasetRef?> GetByIdAsync(string datasetId, CancellationToken ct)
    {
        var entity = await db.Datasets.FindAsync([datasetId], ct);
        if (entity is null) return null;
        return Map(entity);
    }

    public async Task<IReadOnlyList<DatasetRef>> GetAllAsync(CancellationToken ct)
    {
        return await db.Datasets
            .OrderByDescending(e => e.CreatedAtUtc)
            .Select(e => Map(e))
            .ToListAsync(ct);
    }

    public async Task<string?> GetContentHashAsync(
        IReadOnlyList<string> symbols, IReadOnlyList<string> timeframes,
        DateTime fromUtc, DateTime toUtc, DatasetGranularity granularity, CancellationToken ct)
    {
        var symJson = JsonSerializer.Serialize(symbols.OrderBy(s => s), JsonOptions);
        var tfJson = JsonSerializer.Serialize(timeframes.OrderBy(t => t), JsonOptions);
        var gran = granularity.ToString();
        return await db.Datasets
            .Where(e => e.Symbols == symJson && e.Timeframes == tfJson
                && e.FromUtc == fromUtc && e.ToUtc == toUtc && e.Granularity == gran)
            .Select(e => (string?)e.ContentHash)
            .FirstOrDefaultAsync(ct);
    }

    private static DatasetRef Map(DatasetEntity e) => new(
        e.Id,
        e.ContentHash,
        JsonSerializer.Deserialize<IReadOnlyList<string>>(e.Symbols, JsonOptions) ?? [],
        JsonSerializer.Deserialize<IReadOnlyList<string>>(e.Timeframes, JsonOptions) ?? [],
        e.FromUtc,
        e.ToUtc,
        Enum.TryParse<DatasetGranularity>(e.Granularity, out var g) ? g : DatasetGranularity.Bar,
        e.RowCount);
}
