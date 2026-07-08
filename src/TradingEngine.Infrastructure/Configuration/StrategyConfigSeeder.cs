using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingEngine.Infrastructure.Persistence;

namespace TradingEngine.Infrastructure.Configuration;

public sealed class StrategyConfigSeeder
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _basePath;
    private readonly ILogger<StrategyConfigSeeder> _logger;

    public StrategyConfigSeeder(
        IServiceScopeFactory scopeFactory,
        string basePath,
        ILogger<StrategyConfigSeeder> logger)
    {
        _scopeFactory = scopeFactory;
        _basePath = basePath;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        // P2.6 (D9, units doctrine): lint the FILES on disk unconditionally, even when the DB is already
        // seeded and the rest of this method bails out below — a hand-edit to a raw-pip field without its
        // normalized companion must fail startup every time, not just on the very first seed.
        var violations = ConfigLinter.LintDirectories(
            Path.Combine(_basePath, "config", "strategies"),
            Path.Combine(_basePath, "config", "risk-profiles"));
        if (violations.Count > 0)
        {
            throw new InvalidOperationException(
                "Config lint failed:\n" + string.Join('\n', violations));
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IStrategyConfigStore>();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        await db.Database.EnsureCreatedAsync(ct);

        var existing = await store.GetAllAsync(ct);
        if (existing.Count > 0)
        {
            _logger.LogInformation("Strategy config store already has {Count} entries, skipping seed", existing.Count);
            return;
        }

        _logger.LogInformation("Seeding strategy configs from JSON files");
        var entries = LoadFromJson();
        foreach (var entry in entries)
        {
            await store.UpsertAsync(entry, ct);
            _logger.LogInformation("Seeded strategy config: {Id}", entry.Id);
        }
        _logger.LogInformation("Seeded {Count} strategy config(s)", entries.Count);
    }

    private List<StrategyConfigEntry> LoadFromJson()
    {
        var dir = Path.Combine(_basePath, "config", "strategies");
        if (!Directory.Exists(dir))
        {
            _logger.LogWarning("Strategy config directory not found: {Dir}", dir);
            return [];
        }

        var results = new List<StrategyConfigEntry>();
        foreach (var file in Directory.GetFiles(dir, "*.json"))
            results.Add(ParseFile(file));
        return results;
    }

    /// <summary>
    /// Parse a single <c>config/strategies/*.json</c> file into a <see cref="StrategyConfigEntry"/>.
    /// Shared by the first-time seeder and the P1.2 <c>ConfigSyncService</c> propagation path so both read
    /// exactly the same shape.
    /// </summary>
    public static StrategyConfigEntry ParseFile(string file)
    {
        var jsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };

        var json = File.ReadAllText(file);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Clone() detaches the element from `doc`, which is disposed at the end of this method. Without it
        // the stored JsonElement points at a disposed JsonDocument and any later read (GetRawText in the
        // store) throws ObjectDisposedException — crashing startup the first time a fresh DB is seeded.
        var parameters = root.TryGetProperty("parameters", out var p) && p.ValueKind != JsonValueKind.Undefined
            ? p.Clone()
            : default;

        return new StrategyConfigEntry(
            root.GetProperty("id").GetString()!,
            root.GetProperty("displayName").GetString()!,
            root.TryGetProperty("enabled", out var en) && en.GetBoolean(),
            root.TryGetProperty("riskProfileId", out var rp) ? rp.GetString()! : "standard",
            parameters)
        {
            RegimeFilter = ParseFromJson<RegimeFilterOptions>(root, "regimeFilter", jsonOpts),
            OrderEntry = ParseFromJson<OrderEntryOptions>(root, "orderEntry", jsonOpts),
            PositionManagement = ParseFromJson<PositionManagementOptions>(root, "positionManagement", jsonOpts),
            Reentry = ParseFromJson<ReentryOptions>(root, "reentry", jsonOpts),
            Thesis = root.TryGetProperty("thesis", out var th) ? th.GetString() : null,
            ExpectedTradesPerWeek = root.TryGetProperty("expectedTradesPerWeek", out var etpw) ? etpw.GetInt32() : null,
            ExpectedHoldBars = root.TryGetProperty("expectedHoldBars", out var ehb) ? ehb.GetInt32() : null,
        };
    }

    private static T? ParseFromJson<T>(JsonElement root, string propertyName, JsonSerializerOptions opts)
        where T : class
    {
        if (!root.TryGetProperty(propertyName, out var elem)) return null;
        return JsonSerializer.Deserialize<T>(elem.GetRawText(), opts);
    }
}
