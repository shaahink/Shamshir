using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingEngine.Domain.Experiments;
using TradingEngine.Infrastructure.Adapters;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Host.Experiments;

public sealed class ExperimentRunner
{
    private readonly IBarRepository _barRepo;
    private readonly IPassProbabilityEstimator _passEstimator;
    private readonly IExperimentRepository _experimentRepo;
    private readonly IBacktestRunRepository _backtestRunRepo;
    private readonly ITradeRepository _tradeRepo;
    private readonly IEquityRepository _equityRepo;
    private readonly ISymbolInfoRegistry _symbolRegistry;
    private readonly string _solutionRoot;
    private readonly ILogger<ExperimentRunner> _logger;

    public ExperimentRunner(
        IBarRepository barRepo,
        IPassProbabilityEstimator passEstimator,
        IExperimentRepository experimentRepo,
        IBacktestRunRepository backtestRunRepo,
        ITradeRepository tradeRepo,
        IEquityRepository equityRepo,
        ISymbolInfoRegistry symbolRegistry,
        ILogger<ExperimentRunner> logger)
    {
        _barRepo = barRepo;
        _passEstimator = passEstimator;
        _experimentRepo = experimentRepo;
        _backtestRunRepo = backtestRunRepo;
        _tradeRepo = tradeRepo;
        _equityRepo = equityRepo;
        _symbolRegistry = symbolRegistry;
        _solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        _logger = logger;
    }

    public async Task<ExperimentResult> RunAsync(ExperimentSpec spec, CancellationToken ct)
    {
        var experimentId = Guid.NewGuid();
        _logger.LogInformation("Experiment {Id} starting: {Name}", experimentId, spec.Name);

        try
        {
            await ValidateBarsAsync(spec, ct);

            await CreateExperimentAsync(experimentId, spec, ct);

            var folds = spec.WalkForward is not null
                ? WalkForwardSplitter.Split(spec.From, spec.To, spec.WalkForward)
                : [(spec.From, spec.To, spec.From, spec.To)];

            var totalRuns = spec.Variants.Length * folds.Count;
            if (totalRuns > spec.MaxRuns)
            {
                throw new InvalidOperationException(
                    $"Experiment requires {totalRuns} runs, exceeds MaxRuns={spec.MaxRuns}");
            }

            var configLoader = new ConfigLoader(_solutionRoot);
            var baseConfig = configLoader.Load();

            var allScores = new List<VariantScore>();

            var runIndex = 0;
            foreach (var variant in spec.Variants)
            {
                var overrides = variant.Overrides is not null
                    ? new Dictionary<string, JsonElement>(variant.Overrides)
                    : [];

                var variantConfig = ConfigOverrideApplier.Apply(baseConfig, overrides);

                var variantFolds = new List<FoldScore>();

                for (var fi = 0; fi < folds.Count; fi++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (trainFrom, trainTo, testFrom, testTo) = folds[fi];
                    var foldRole = folds.Count == 1 ? "Test" : "Train";
                    var foldLabel = folds.Count == 1 ? "Full" : $"Fold{fi}";

                    var trainRunId = $"{experimentId:N[..8]}-{runIndex++}";
                    await RunSingleAsync(
                        variantConfig, spec, variant.Label,
                        trainFrom, trainTo, foldRole, fi,
                        trainRunId, experimentId, ct);

                    if (folds.Count > 1)
                    {
                        var testRunId = $"{experimentId:N[..8]}-{runIndex++}";
                        await RunSingleAsync(
                            variantConfig, spec, variant.Label,
                            testFrom, testTo, "Test", fi,
                            testRunId, experimentId, ct);
                    }

                    var trades = await _tradeRepo.GetByDateRangeAsync(
                        testFrom.ToDateTime(TimeOnly.MinValue),
                        testTo.ToDateTime(TimeOnly.MaxValue),
                        ct);

                    var equity = await _equityRepo.GetByDateRangeAsync(
                        testFrom.ToDateTime(TimeOnly.MinValue),
                        testTo.ToDateTime(TimeOnly.MaxValue),
                        ct);

                    var rules = variantConfig.PropFirms.First();
                    var foldScore = VariantScorer.ScoreFold(
                        trades, equity, rules, fi, foldRole, _passEstimator);
                    variantFolds.Add(foldScore);

                    _logger.LogInformation("  Fold {Index} {Role}: composite={Composite:F3} trades={Trades}",
                        fi, foldRole, foldScore.Composite, foldScore.TotalTrades);
                }

                var variantScore = VariantScorer.ScoreVariant(
                    variant.Label, variantFolds,
                    spec.Scoring ?? new ScoringWeights());
                allScores.Add(variantScore);

                _logger.LogInformation("Variant {Label}: composite={Composite:F3}", variant.Label, variantScore.Composite);
            }

            await _experimentRepo.UpdateAsync(new ExperimentEntity
            {
                Id = experimentId,
                Name = spec.Name,
                Hypothesis = spec.Hypothesis,
                SpecJson = JsonSerializer.Serialize(spec),
                Status = "Completed",
                CreatedUtc = DateTime.UtcNow,
                CompletedUtc = DateTime.UtcNow,
            }, ct);

            await ExperimentReportWriter.WriteAsync(
                spec, experimentId, allScores, _solutionRoot, ct);

            _logger.LogInformation("Experiment {Id} completed: {Name}", experimentId, spec.Name);

            return new ExperimentResult(experimentId, spec.Name, true, null, allScores);
        }
        catch (OperationCanceledException)
        {
            await MarkFailed(experimentId, spec, "Cancelled", ct);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Experiment {Id} failed: {Name}", experimentId, spec.Name);
            await MarkFailed(experimentId, spec, ex.Message, ct);
            return new ExperimentResult(experimentId, spec.Name, false, ex.Message, []);
        }
    }

    private async Task ValidateBarsAsync(ExperimentSpec spec, CancellationToken ct)
    {
        var missing = new List<string>();
        foreach (var symbolName in spec.Symbols)
        {
            var symbol = Symbol.Parse(symbolName);
            foreach (var tfName in spec.Timeframes)
            {
                var tf = Enum.TryParse<Timeframe>(tfName, out var t) ? t : Timeframe.H1;
                var from = spec.From.ToDateTime(TimeOnly.MinValue);
                var to = spec.To.ToDateTime(TimeOnly.MaxValue);
                var bars = await _barRepo.GetAsync(symbol, tf, from, to, ct);
                if (bars.Count == 0)
                    missing.Add($"{symbolName}/{tfName} [{spec.From:yyyy-MM-dd}–{spec.To:yyyy-MM-dd}]");
            }
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"No bars found for: {string.Join(", ", missing)}. Seed data first with scripts/seed-bars.ps1.");
        }
    }

    private async Task RunSingleAsync(
        LoadedConfig config,
        ExperimentSpec spec,
        string variantLabel,
        DateOnly runFrom,
        DateOnly runTo,
        string foldRole,
        int foldIndex,
        string runId,
        Guid experimentId,
        CancellationToken ct)
    {
        var primarySymbol = Symbol.Parse(spec.Symbols[0]);
        var timeframe = Enum.TryParse<Timeframe>(spec.Timeframes[0], out var tf) ? tf : Timeframe.H1;
        var from = runFrom.ToDateTime(TimeOnly.MinValue);
        var to = runTo.ToDateTime(TimeOnly.MaxValue);
        var dbPath = Path.Combine(Path.GetTempPath(), $"exp_{experimentId:N}_{runId}.db");

        var host = EngineHostFactory.Create(new EngineHostOptions
        {
            RunId = runId,
            Mode = EngineMode.Backtest,
            AdapterFactory = sp => new BacktestReplayAdapter(
                _barRepo, primarySymbol, timeframe, from, to,
                100_000m,
                sp.GetRequiredService<ISymbolInfoRegistry>(),
                sp.GetRequiredService<Func<string, string, decimal>>(),
                sp.GetRequiredService<ILogger<BacktestReplayAdapter>>()),
            DbPath = dbPath,
            SolutionRoot = _solutionRoot,
            SymbolNames = spec.Symbols.ToList(),
            MinLogLevel = LogLevel.Warning,
        });

        EngineHostFactory.WireEventHandlers(host);
        EngineHostFactory.WireRiskRules(host);

        await host.StartAsync(ct);

        try
        {
            var adapter = host.Services.GetRequiredService<IBrokerAdapter>();
            await adapter.BarStream.Completion;

            var barHandler = host.Services.GetRequiredService<BarEvaluationHandler>();
            await barHandler.FlushRemainingAsync();

            await Task.Delay(2_000, ct);

            var summary = await _backtestRunRepo.GetByIdAsync(runId, ct);
            if (summary is not null)
            {
                await _experimentRepo.AddRunAsync(new ExperimentRunEntity
                {
                    Id = Guid.NewGuid(),
                    ExperimentId = experimentId,
                    BacktestRunId = runId,
                    VariantLabel = variantLabel,
                    FoldIndex = foldIndex,
                    FoldRole = foldRole,
                }, ct);
            }
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
            TryDeleteDb(dbPath);
        }
    }

    private async Task CreateExperimentAsync(Guid id, ExperimentSpec spec, CancellationToken ct)
    {
        await _experimentRepo.CreateAsync(new ExperimentEntity
        {
            Id = id,
            Name = spec.Name,
            Hypothesis = spec.Hypothesis,
            SpecJson = JsonSerializer.Serialize(spec),
            Status = "Running",
            CreatedUtc = DateTime.UtcNow,
        }, ct);
    }

    private async Task MarkFailed(Guid id, ExperimentSpec spec, string error, CancellationToken ct)
    {
        try
        {
            await _experimentRepo.UpdateAsync(new ExperimentEntity
            {
                Id = id,
                Name = spec.Name,
                Hypothesis = spec.Hypothesis,
                SpecJson = JsonSerializer.Serialize(spec),
                Status = $"Failed: {error}",
                CreatedUtc = DateTime.UtcNow,
                CompletedUtc = DateTime.UtcNow,
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark experiment {Id} as failed", id);
        }
    }

    private static void TryDeleteDb(string path)
    {
        for (var i = 0; i < 5; i++)
        {
            try { if (File.Exists(path)) File.Delete(path); break; }
            catch { Task.Delay(200).GetAwaiter().GetResult(); }
        }
    }
}
