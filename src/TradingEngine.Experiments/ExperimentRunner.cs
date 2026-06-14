namespace TradingEngine.Experiments;

public sealed class ExperimentRunner
{
    private readonly IBarRepository _barRepo;
    private readonly IPassProbabilityEstimator _passEstimator;
    private readonly IExperimentRepository _experimentRepo;
    private readonly IBacktestRunRepository _backtestRunRepo;
    private readonly ITradeRepository _tradeRepo;
    private readonly IEquityRepository _equityRepo;
    private readonly ISymbolInfoRegistry _symbolRegistry;
    private readonly IExperimentHostFactory _hostFactory;
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
        IExperimentHostFactory hostFactory,
        ILogger<ExperimentRunner> logger)
    {
        _barRepo = barRepo;
        _passEstimator = passEstimator;
        _experimentRepo = experimentRepo;
        _backtestRunRepo = backtestRunRepo;
        _tradeRepo = tradeRepo;
        _equityRepo = equityRepo;
        _symbolRegistry = symbolRegistry;
        _hostFactory = hostFactory;
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

                    var trainRunId = $"{experimentId.ToString("N")[..8]}-{runIndex++}";
                    var (trainTrades, trainEquity) = await RunSingleAsync(
                        variantConfig, spec, variant.Label,
                        trainFrom, trainTo, "Train", fi,
                        trainRunId, experimentId, ct);

                    if (folds.Count > 1)
                    {
                        var testRunId = $"{experimentId.ToString("N")[..8]}-{runIndex++}";
                        var (testTrades, testEquity) = await RunSingleAsync(
                            variantConfig, spec, variant.Label,
                            testFrom, testTo, "Test", fi,
                            testRunId, experimentId, ct);

                        var rules = variantConfig.PropFirms.First();
                        variantFolds.Add(VariantScorer.ScoreFold(
                            trainTrades, trainEquity, rules, fi, "Train", _passEstimator));
                        variantFolds.Add(VariantScorer.ScoreFold(
                            testTrades, testEquity, rules, fi, "Test", _passEstimator));

                        _logger.LogInformation("  Fold {Index}: Train composite={Train:F3} Test composite={Test:F3}",
                            fi,
                            variantFolds[^2].Composite,
                            variantFolds[^1].Composite);
                    }
                    else
                    {
                        var rules = variantConfig.PropFirms.First();
                        var foldScore = VariantScorer.ScoreFold(
                            trainTrades, trainEquity, rules, fi, "Test", _passEstimator);
                        variantFolds.Add(foldScore);

                        _logger.LogInformation("  Fold {Index} {Role}: composite={Composite:F3} trades={Trades}",
                            fi, foldScore.FoldRole, foldScore.Composite, foldScore.TotalTrades);
                    }
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

    private async Task<(IReadOnlyList<TradeResult> Trades, IReadOnlyList<EquitySnapshot> Equity)> RunSingleAsync(
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
        var dbPath = Path.Combine(Path.GetTempPath(), $"exp_{experimentId:N}.db");

        var host = _hostFactory.Create(new EngineHostOptions
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
            PreloadedConfig = config,
        });

        _hostFactory.WireEventHandlers(host);
        _hostFactory.WireRiskRules(host);

        await host.StartAsync(ct);

        var runTrades = new List<TradeResult>();
        var runEquity = new List<EquitySnapshot>();

        try
        {
            var adapter = host.Services.GetRequiredService<IBrokerAdapter>();
            await adapter.BarStream.Completion;

            var barHandler = host.Services.GetRequiredService<BarEvaluationHandler>();
            await barHandler.FlushRemainingAsync();

            await Task.Delay(2_000, ct);

            try
            {
                var scopedTradeRepo = host.Services.GetRequiredService<ITradeRepository>();
                runTrades = (await scopedTradeRepo.GetByDateRangeAsync(from, to, ct)).ToList();
                var scopedEquityRepo = host.Services.GetRequiredService<IEquityRepository>();
                runEquity = (await scopedEquityRepo.GetByDateRangeAsync(from, to, ct)).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to query run data for scoring {RunId}", runId);
            }

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

        return (runTrades.AsReadOnly(), runEquity.AsReadOnly());
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
