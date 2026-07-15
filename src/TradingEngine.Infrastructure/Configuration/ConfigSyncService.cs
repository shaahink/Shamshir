using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence;

namespace TradingEngine.Infrastructure.Configuration;

public enum ConfigDriftStatus
{
    /// <summary>File hash matches the last-synced hash — nothing to do.</summary>
    InSync,
    /// <summary>Row had no recorded seed hash yet (pre-M42) — hash baselined, content untouched.</summary>
    Baselined,
    /// <summary>File changed on disk and the row was not hand-edited — JSON re-applied to the DB.</summary>
    Resynced,
    /// <summary>File changed on disk BUT the row was hand-edited via UI since the last seed — NOT clobbered.</summary>
    HandEditedConflict,
}

/// <summary>One config item's drift state (strategy or risk profile).</summary>
public sealed record ConfigDriftEntry(
    string ConfigType,
    string Id,
    ConfigDriftStatus Status,
    string? FileHash,
    string? DbSeededHash,
    DateTime? UpdatedAtUtc,
    DateTime? SeededAtUtc);

public sealed record ConfigSyncResult(IReadOnlyList<ConfigDriftEntry> Entries)
{
    public int InSync => Entries.Count(e => e.Status == ConfigDriftStatus.InSync);
    public int Baselined => Entries.Count(e => e.Status == ConfigDriftStatus.Baselined);
    public int Resynced => Entries.Count(e => e.Status == ConfigDriftStatus.Resynced);
    public int Conflicts => Entries.Count(e => e.Status == ConfigDriftStatus.HandEditedConflict);
    public IReadOnlyList<ConfigDriftEntry> Drift =>
        Entries.Where(e => e.Status == ConfigDriftStatus.HandEditedConflict).ToList();
}

/// <summary>
/// P1.2 (AUDIT F9): propagate <c>config/strategies/*.json</c> and <c>config/risk-profiles/*.json</c> edits
/// into the DB on startup, while never clobbering a UI/hand edit. The seeder only ever seeds once, so every
/// JSON edit the agent made after the first seed stayed inert (the whole limit-order rollout was invisible).
///
/// Policy per file: hash the file, compare to the DB row's <c>SeededHash</c>.
/// <list type="bullet">
/// <item>hash == SeededHash → in sync, no-op.</item>
/// <item>SeededHash null (first run after M42) → baseline the hash, leave content untouched.</item>
/// <item>hash ≠ SeededHash AND the row was NOT hand-edited (UpdatedAtUtc ≤ SeededAtUtc + tolerance) →
///   re-apply the JSON (bump Version) and record the new hash.</item>
/// <item>hash ≠ SeededHash AND the row WAS hand-edited → drift conflict: leave the DB row, surface it.</item>
/// </list>
/// </summary>
public sealed class ConfigSyncService
{
    // The audit interceptor re-stamps UpdatedAtUtc on the very save that records SeededAtUtc, so the two
    // land a few ms apart on a legitimate sync. A real hand edit happens minutes/hours later, so a small
    // tolerance cleanly separates "auto-synced" from "hand-edited" without a timing race.
    private static readonly TimeSpan HandEditTolerance = TimeSpan.FromSeconds(5);

    private readonly TradingDbContext _db;
    private readonly IStrategyConfigStore _strategyStore;
    private readonly IRiskProfileStore _riskStore;
    private readonly string _basePath;
    private readonly ILogger<ConfigSyncService> _logger;

    private static readonly JsonSerializerOptions RiskJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public ConfigSyncService(
        TradingDbContext db,
        IStrategyConfigStore strategyStore,
        IRiskProfileStore riskStore,
        string basePath,
        ILogger<ConfigSyncService> logger)
    {
        _db = db;
        _strategyStore = strategyStore;
        _riskStore = riskStore;
        _basePath = basePath;
        _logger = logger;
    }

    /// <summary>Apply the propagation policy (mutates the DB). Returns every item's resulting status.</summary>
    public async Task<ConfigSyncResult> SyncAsync(CancellationToken ct = default)
        => await RunAsync(apply: true, ct);

    /// <summary>Read-only drift report for <c>GET /api/system/config-drift</c> (never mutates).</summary>
    public async Task<ConfigSyncResult> DetectDriftAsync(CancellationToken ct = default)
        => await RunAsync(apply: false, ct);

    private async Task<ConfigSyncResult> RunAsync(bool apply, CancellationToken ct)
    {
        var entries = new List<ConfigDriftEntry>();
        entries.AddRange(await SyncStrategiesAsync(apply, ct));
        entries.AddRange(await SyncRiskProfilesAsync(apply, ct));

        var result = new ConfigSyncResult(entries);
        if (apply && (result.Resynced > 0 || result.Conflicts > 0))
        {
            _logger.LogInformation(
                "Config sync: {InSync} in-sync, {Baselined} baselined, {Resynced} re-synced from JSON, "
                + "{Conflicts} hand-edited conflict(s).",
                result.InSync, result.Baselined, result.Resynced, result.Conflicts);
        }
        return result;
    }

    private async Task<List<ConfigDriftEntry>> SyncStrategiesAsync(bool apply, CancellationToken ct)
    {
        var dir = Path.Combine(_basePath, "config", "strategies");
        var entries = new List<ConfigDriftEntry>();
        if (!Directory.Exists(dir)) return entries;

        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            var fileHash = HashFile(file);
            var entry = StrategyConfigSeeder.ParseFile(file);
            var row = await _db.StrategyConfigs.FindAsync([entry.Id], ct);

            var status = Classify(row?.SeededHash, fileHash, row?.UpdatedAtUtc, row?.SeededAtUtc, exists: row is not null);

            if (apply && status is ConfigDriftStatus.Resynced)
            {
                await _strategyStore.UpsertAsync(entry, ct);
                await StampSeedAsync(entry.Id, fileHash, ct);
                _logger.LogInformation("Config sync: re-applied strategy {Id} from JSON (Version bumped).", entry.Id);
            }
            else if (apply && status is ConfigDriftStatus.Baselined && row is not null)
            {
                row.SeededHash = fileHash;
                row.SeededAtUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
            }
            else if (status is ConfigDriftStatus.HandEditedConflict)
            {
                _logger.LogWarning(
                    "Config drift: strategy {Id} changed on disk but was hand-edited via UI since seed "
                    + "(UpdatedAtUtc {Updated:o} > SeededAtUtc {Seeded:o}). JSON change NOT applied.",
                    entry.Id, row!.UpdatedAtUtc, row.SeededAtUtc);
            }

            entries.Add(new ConfigDriftEntry(
                "strategy", entry.Id, status, fileHash, row?.SeededHash, row?.UpdatedAtUtc, row?.SeededAtUtc));
        }
        return entries;
    }

    private async Task<List<ConfigDriftEntry>> SyncRiskProfilesAsync(bool apply, CancellationToken ct)
    {
        var dir = Path.Combine(_basePath, "config", "risk-profiles");
        var entries = new List<ConfigDriftEntry>();
        if (!Directory.Exists(dir)) return entries;

        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            var fileHash = HashFile(file);
            var profile = JsonSerializer.Deserialize<RiskProfile>(File.ReadAllText(file), RiskJsonOpts);
            if (profile is null) continue;

            var row = await _db.RiskProfiles.FindAsync([profile.Id], ct);
            var status = Classify(row?.SeededHash, fileHash, row?.UpdatedAtUtc, row?.SeededAtUtc, exists: row is not null);

            if (apply && status is ConfigDriftStatus.Resynced)
            {
                await _riskStore.UpsertAsync(profile, ct);
                await StampRiskSeedAsync(profile.Id, fileHash, ct);
                _logger.LogInformation("Config sync: re-applied risk profile {Id} from JSON.", profile.Id);
            }
            else if (apply && status is ConfigDriftStatus.Baselined && row is not null)
            {
                row.SeededHash = fileHash;
                row.SeededAtUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
            }
            else if (status is ConfigDriftStatus.HandEditedConflict)
            {
                _logger.LogWarning(
                    "Config drift: risk profile {Id} changed on disk but was hand-edited via UI since seed. "
                    + "JSON change NOT applied.", profile.Id);
            }

            entries.Add(new ConfigDriftEntry(
                "risk-profile", profile.Id, status, fileHash, row?.SeededHash, row?.UpdatedAtUtc, row?.SeededAtUtc));
        }
        return entries;
    }

    private static ConfigDriftStatus Classify(
        string? seededHash, string fileHash, DateTime? updatedAtUtc, DateTime? seededAtUtc, bool exists)
    {
        if (!exists) return ConfigDriftStatus.Resynced;         // missing row → treat as (re)apply
        if (seededHash is null) return ConfigDriftStatus.Baselined;
        if (seededHash == fileHash) return ConfigDriftStatus.InSync;

        var handEdited = updatedAtUtc.HasValue && seededAtUtc.HasValue
            && updatedAtUtc.Value > seededAtUtc.Value + HandEditTolerance;
        return handEdited ? ConfigDriftStatus.HandEditedConflict : ConfigDriftStatus.Resynced;
    }

    private async Task StampSeedAsync(string id, string fileHash, CancellationToken ct)
    {
        var row = await _db.StrategyConfigs.FindAsync([id], ct);
        if (row is null) return;
        row.SeededHash = fileHash;
        row.SeededAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private async Task StampRiskSeedAsync(string id, string fileHash, CancellationToken ct)
    {
        var row = await _db.RiskProfiles.FindAsync([id], ct);
        if (row is null) return;
        row.SeededHash = fileHash;
        row.SeededAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>SHA-256 of the file's text, normalized for line endings + trailing whitespace so a git
    /// autocrlf round-trip does not read as a config change.</summary>
    public static string HashFile(string path)
        => HashText(File.ReadAllText(path));

    public static string HashText(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Trim();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes);
    }
}
