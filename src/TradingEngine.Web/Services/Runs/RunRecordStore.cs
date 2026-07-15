using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingEngine.CTraderRunner;
using TradingEngine.Domain;
using TradingEngine.Domain.Interfaces;
using TradingEngine.Infrastructure.Persistence;

namespace TradingEngine.Web.Services;

/// <summary>Aggregate trade economics for a finished (or finalizing) run, read back from the DB.</summary>
public sealed record RunTradeStats(
    decimal NetProfit, decimal GrossPnL, decimal CommissionTotal, decimal SwapTotal,
    decimal MaxDrawdownPct, int TotalTrades, int WinningTrades, double WinRatePct)
{
    public static RunTradeStats Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// The run record's durable read/write side: the start record (content-addressed DatasetId/
/// ConfigSetId identity), the terminal end record, trade-stats aggregation and warnings JSON.
/// Extracted verbatim from BacktestOrchestrator.
/// </summary>
public sealed class RunRecordStore(
    IServiceScopeFactory scopeFactory,
    ILogger<RunRecordStore> logger)
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<RunRecordStore> _logger = logger;

    public async Task WriteStartRecordAsync(string runId, BacktestConfig cfg, DateTime startedAt, string? effectiveConfigJson,
        string? status = null, int? queuePosition = null)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IBacktestRunRepository>();
            // iter-36 K6: content-address the run. DatasetId = hash of the data window spec (symbols/periods/
            // range); ConfigSetId = hash of the resolved effective config. Identical (DatasetId, ConfigSetId,
            // Seed) ⇒ a deterministic re-run; a duplicate keeps DatasetId, gets a new ConfigSetId + ParentRunId.
            var datasetSpec = $"{SymbolsJson(cfg.Symbols)}|{PeriodsJson(cfg.Periods)}|{cfg.Start:O}|{cfg.End:O}";
            var datasetId = TradingEngine.Infrastructure.ConfigSetHash.Compute(datasetSpec);
            // ConfigSetId = hash of EVERYTHING that determines behavior (ReplayModel): the resolved strategy
            // effective config PLUS the run's risk profile / strategy selection / per-strategy overrides — so
            // a duplicate that changes the risk profile gets a genuinely different ConfigSetId (K6).
            var configIdentity = JsonSerializer.Serialize(new
            {
                effective = effectiveConfigJson ?? "{}",
                riskProfileId = cfg.CustomParams.GetValueOrDefault("RiskProfileId"),
                strategyIds = cfg.CustomParams.GetValueOrDefault("StrategyIds"),
                overrides = cfg.CustomParams.GetValueOrDefault("StrategyOverrides"),
                // iter-38 PK3/R1: a pack or the regime-master change the run's behaviour, so they participate
                // in the ConfigSetId identity (different pack/regime ⇒ a genuinely different run, K6).
                usePackId = cfg.CustomParams.GetValueOrDefault("UsePackId"),
                perStrategyPacks = cfg.CustomParams.GetValueOrDefault("PerStrategyPackIds"),
                disableRegime = cfg.CustomParams.GetValueOrDefault("DisableRegime"),
                stripAddOns = cfg.CustomParams.GetValueOrDefault("StripAddOns"),
                // iter-strategy-system P1/P2: the row plan (per-row packs) + governor toggle change behaviour,
                // so they belong in the run's content address.
                runRows = cfg.CustomParams.GetValueOrDefault("RunRows"),
                governorEnabled = cfg.CustomParams.GetValueOrDefault("GovernorEnabled"),
                // iter-redesign P2.2: per-run protection toggle overrides change behaviour (a "Raw" run is a
                // genuinely different run from a guarded one), so they participate in the content address.
                dailyDdEnabled = cfg.CustomParams.GetValueOrDefault("DailyDdEnabled"),
                maxDdEnabled = cfg.CustomParams.GetValueOrDefault("MaxDdEnabled"),
                forceCloseOnBreachEnabled = cfg.CustomParams.GetValueOrDefault("ForceCloseOnBreachEnabled"),
                exposureEnabled = cfg.CustomParams.GetValueOrDefault("ExposureEnabled"),
                budgetEnabled = cfg.CustomParams.GetValueOrDefault("BudgetEnabled"),
                maxPositionsEnabled = cfg.CustomParams.GetValueOrDefault("MaxPositionsEnabled"),
                honestFills = cfg.CustomParams.GetValueOrDefault("HonestFills"),
                recordExcursions = cfg.CustomParams.GetValueOrDefault("RecordExcursions"),
                exitTimeframe = cfg.CustomParams.GetValueOrDefault("ExitTimeframe"),
            });
            var configSetId = TradingEngine.Infrastructure.ConfigSetHash.Compute(configIdentity);
            var parentRunId = cfg.CustomParams.GetValueOrDefault("ParentRunId");
            var summary = new BacktestRunSummary(
                runId, startedAt, DateTime.MinValue,
                cfg.Symbol, cfg.Period, SymbolsJson(cfg.Symbols), PeriodsJson(cfg.Periods), cfg.Start, cfg.End,
                cfg.Balance, "", effectiveConfigJson ?? "{}", effectiveConfigJson,
                0, 0, 0, 0, 0, 0, 0, 0, -1, null,
                ReportJsonPath: null, DatasetId: datasetId, ConfigSetId: configSetId, Seed: 42,
                ParentRunId: string.IsNullOrWhiteSpace(parentRunId) ? null : parentRunId,
                RunPlanJson: cfg.CustomParams.GetValueOrDefault("RunRows") ?? "[]",
                Venue: cfg.CustomParams.GetValueOrDefault("Venue") ?? "replay",
                RiskProfileId: cfg.CustomParams.GetValueOrDefault("RiskProfileId"),
                GovernorEnabled: cfg.CustomParams.GetValueOrDefault("GovernorEnabled") != "false",
                RegimeEnabled: cfg.CustomParams.GetValueOrDefault("DisableRegime") != "true",
                CommissionPerMillion: cfg.CommissionPerMillion,
                SpreadPips: cfg.SpreadPips,
                ExplorationMode: cfg.CustomParams.GetValueOrDefault("ExplorationMode") == "true",
                RecordExcursions: cfg.CustomParams.GetValueOrDefault("RecordExcursions") == "true",
                Status: status,
                QueuePosition: queuePosition);
            await repo.SaveAsync(summary, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write start record for {RunId}", runId);
        }
    }

    public async Task<bool> WriteEndRecordAsync(
        string runId, BacktestConfig cfg, DateTime startedAt,
        BacktestResult result, RunTradeStats stats, string? effectiveConfigJson, string? status = null)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IBacktestRunRepository>();
            var wallElapsedMs = result.WallElapsedMs > 0
                ? result.WallElapsedMs
                : (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
            var totalBars = result.TotalBars;
            var barsPerSec = result.BarsPerSec > 0
                ? result.BarsPerSec
                : wallElapsedMs > 0 ? totalBars / (wallElapsedMs / 1000.0) : 0;
            var summary = new BacktestRunSummary(
                runId, startedAt, DateTime.UtcNow,
                cfg.Symbol, cfg.Period, SymbolsJson(cfg.Symbols), PeriodsJson(cfg.Periods), cfg.Start, cfg.End,
                cfg.Balance, result.AlgoHash, effectiveConfigJson ?? "{}", effectiveConfigJson,
                stats.NetProfit, stats.GrossPnL, stats.CommissionTotal, stats.SwapTotal, stats.MaxDrawdownPct,
                stats.TotalTrades, stats.WinningTrades, stats.WinRatePct,
                result.ExitCode, result.ErrorMessage,
                result.ReportJsonPath,
                WallElapsedMs: wallElapsedMs,
                BarsPerSec: barsPerSec,
                TotalBars: totalBars,
                WarningsJson: result.WarningsJson,
                ComparePairId: cfg.CustomParams.GetValueOrDefault("ComparePairId"),
                ParentRunId: cfg.CustomParams.GetValueOrDefault("ParentRunId"),
                ExplorationMode: cfg.CustomParams.GetValueOrDefault("ExplorationMode") == "true",
                RecordExcursions: cfg.CustomParams.GetValueOrDefault("RecordExcursions") == "true",
                Status: status);
            await repo.UpdateAsync(summary, CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write end record for {RunId}", runId);
            return false;
        }
    }

    // P0.2 (F5, Q5): serialize the run's collected teardown/persistence warnings into a JSON array,
    // merging any warnings already carried on the result (e.g. from the cTrader leg). Returns null when
    // there are none, so a clean run keeps WarningsJson NULL and resolves to plain `completed`.
    public static string? MergeWarningsJson(BacktestRunState state, string? existingJson)
    {
        var warnings = new List<RunWarning>();

        if (RunStatusResolver.HasWarnings(existingJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<RunWarning>>(existingJson!);
                if (parsed is not null) warnings.AddRange(parsed);
            }
            catch { /* malformed prior warnings must never break finalization */ }
        }

        while (state.Warnings.TryDequeue(out var w))
            warnings.Add(w);

        return warnings.Count == 0 ? null : JsonSerializer.Serialize(warnings);
    }

    public async Task<RunTradeStats> GetTradeStatsAsync(string runId, decimal initialBalance)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var trades = await db.Trades
                .Where(t => t.RunId == runId)
                .OrderBy(t => t.ClosedAtUtc)
                .ToListAsync();

            if (trades.Count == 0) return RunTradeStats.Empty;

            var netPnL = trades.Sum(t => t.NetPnLAmount);
            var grossPnL = trades.Sum(t => t.GrossPnLAmount);
            var commissionTotal = trades.Sum(t => t.CommissionAmount);
            var swapTotal = trades.Sum(t => t.SwapAmount);
            var wins = trades.Count(t => t.NetPnLAmount > 0);
            var winRate = (double)wins / trades.Count;

            // Max drawdown from the engine's per-bar equity snapshots. Materialize first
            // then compute max to avoid EF Core translation issues with DefaultIfEmpty on nullable.
            var snapshotDds = await db.EquitySnapshots
                .Where(s => s.RunId == runId)
                .Select(s => (decimal?)s.CurrentMaxDrawdown)
                .ToListAsync();
            var snapshotDd = snapshotDds.Count > 0 ? snapshotDds.Max().GetValueOrDefault() : 0m;

            var equity = initialBalance;
            var peak = initialBalance;
            var tradeDd = 0m;
            foreach (var t in trades)
            {
                equity += t.NetPnLAmount;
                if (equity > peak) peak = equity;
                if (peak > 0)
                {
                    var dd = (peak - equity) / peak;
                    if (dd > tradeDd) tradeDd = dd;
                }
            }

            return new(netPnL, grossPnL, commissionTotal, swapTotal, snapshotDd > 0 ? snapshotDd : tradeDd, trades.Count, wins, winRate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query trade stats for {RunId}", runId);
            return RunTradeStats.Empty;
        }
    }

    private static string SymbolsJson(IReadOnlyList<string> symbols) =>
        JsonSerializer.Serialize(symbols ?? []);

    private static string PeriodsJson(IReadOnlyList<string> periods) =>
        JsonSerializer.Serialize(periods ?? []);
}
