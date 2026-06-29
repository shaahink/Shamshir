using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Infrastructure.Configuration;

public sealed class JsonExportService
{
    private readonly IStrategyConfigStore _store;
    private readonly string _basePath;
    private readonly ILogger<JsonExportService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public JsonExportService(
        IStrategyConfigStore store,
        string basePath,
        ILogger<JsonExportService> logger)
    {
        _store = store;
        _basePath = basePath;
        _logger = logger;
    }

    public async Task ExportAllAsync(CancellationToken ct = default)
    {
        var entries = await _store.GetAllAsync(ct);
        var dir = Path.Combine(_basePath, "config", "strategies");
        Directory.CreateDirectory(dir);

        foreach (var entry in entries)
        {
            var fileName = $"{entry.Id}.json";
            var filePath = Path.Combine(dir, fileName);

            var export = new StrategyExportDto
            {
                Id = entry.Id,
                DisplayName = entry.DisplayName,
                Enabled = entry.Enabled,
                RiskProfileId = entry.RiskProfileId,
                Parameters = DeserializeElement(entry.Parameters),
                RegimeFilter = entry.RegimeFilter,
                OrderEntry = entry.OrderEntry,
                PositionManagement = entry.PositionManagement,
                Reentry = entry.Reentry,
            };

            var json = JsonSerializer.Serialize(export, JsonOptions);
            await File.WriteAllTextAsync(filePath, json, ct);
            _logger.LogInformation("Exported strategy config: {Id} -> {Path}", entry.Id, filePath);
        }

        _logger.LogInformation("Exported {Count} strategy config(s)", entries.Count);
    }

    private static JsonElement DeserializeElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Undefined)
            return default;
        using var doc = JsonDocument.Parse(element.GetRawText());
        return doc.RootElement.Clone();
    }

    private sealed class StrategyExportDto
    {
        public string Id { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public bool Enabled { get; set; }
        public string RiskProfileId { get; set; } = "";
        public JsonElement Parameters { get; set; }
        public RegimeFilterOptions? RegimeFilter { get; set; }
        public OrderEntryOptions? OrderEntry { get; set; }
        public PositionManagementOptions? PositionManagement { get; set; }
        public ReentryOptions? Reentry { get; set; }
    }
}
