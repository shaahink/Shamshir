namespace TradingEngine.Experiments;

public sealed class ExperimentRunner
{
    private readonly IMarketDataStore _marketDataStore;
    private readonly IPassProbabilityEstimator _passEstimator;
    private readonly IExperimentRepository _experimentRepo;
    private readonly ISymbolInfoRegistry _symbolRegistry;
    private readonly IExperimentHostFactory _hostFactory;
    private readonly string _solutionRoot;
    private readonly ILogger<ExperimentRunner> _logger;

    public ExperimentRunner(
        IMarketDataStore marketDataStore,
        IPassProbabilityEstimator passEstimator,
        IExperimentRepository experimentRepo,
        ISymbolInfoRegistry symbolRegistry,
        IExperimentHostFactory hostFactory,
        ILogger<ExperimentRunner> logger)
    {
        _marketDataStore = marketDataStore;
        _passEstimator = passEstimator;
        _experimentRepo = experimentRepo;
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
                var rules = variantConfig.PropFirms.First();

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

                        var trainScore = VariantScorer.ScoreFold(
                            trainTrades, trainEquity, rules, fi, "Train", _passEstimator);
                        var testScore = VariantScorer.ScoreFold(
                            testTrades, testEquity, rules, fi, "Test", _passEstimator);
                        variantFolds.Add(trainScore);
                        variantFolds.Add(testScore);

                        await PersistRunAsync(experimentId, variant.Label, trainRunId, trainScore, ct);
                        await PersistRunAsync(experimentId, variant.Label, testRunId, testScore, ct);

                        _logger.LogInformation("  Fold {Index}: Train composite={Train:F3} Test composite={Test:F3}",
                            fi, trainScore.Composite, testScore.Composite);
                    }
                    else
                    {
                        var foldScore = VariantScorer.ScoreFold(
                            trainTrades, trainEquity, rules, fi, "Test", _passEstimator);
                        variantFolds.Add(foldScore);

                        await PersistRunAsync(experimentId, variant.Label, trainRunId, foldScore, ct);

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
                var bars = await _marketDataStore.ReadBarsAsync(symbol, tf, from, to, ct);
                if (bars.Count == 0)
                    missing.Add($"{symbolName}/{tfName} [{spec.From:yyyy-MM-dd}–{spec.To:yyyy-MM-dd}]");
            }
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"No tape data found for: {string.Join(", ", missing)}. Import or download market data first (Data Manager).");
        }
    }

    // Requested strategies must actually trade regardless of their stored `enabled` flag in
    // config/strategies/*.json — a strategy the owner explicitly listed in the spec is an
    // unconditional ask, not a suggestion the base config can silently veto.
    private static LoadedConfig ForceEnableStrategies(LoadedConfig config, IReadOnlyCollection<string> strategyIds)
    {
        if (strategyIds.Count == 0) return config;
        var ids = new HashSet<string>(strategyIds, StringComparer.Ordinal);
        return config with
        {
            StrategyConfigs = config.StrategyConfigs
                .Select(c => ids.Contains(c.Id) ? c with { Enabled = true } : c)
                .ToList(),
        };
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
        var runConfig = ForceEnableStrategies(config, spec.Strategies);

        var host = _hostFactory.Create(new EngineHostOptions
        {
            RunId = runId,
            Mode = EngineMode.Backtest,
            // iter-tape-enable Tier1: experiments read from the canonical tape store (marketdata.db) via
            // TapeReplayAdapter, the SAME venue the New-Backtest "tape" path uses — not the per-run/catalog
            // Bars table, which requires cTrader downloads and is invisible to imported market data.
            AdapterFactory = sp => new TapeReplayAdapter(
                _marketDataStore, primarySymbol, timeframe, Timeframe.M1, from, to,
                100_000m,
                sp.GetRequiredService<ISymbolInfoRegistry>(),
                sp.GetRequiredService<Func<string, string, decimal>>(),
                sp.GetRequiredService<ILogger<TapeReplayAdapter>>()),
            DbPath = dbPath,
            SolutionRoot = _solutionRoot,
            SymbolNames = spec.Symbols.ToList(),
            // Honour exactly the strategies the spec asked for — empty ActiveStrategyIds means "every
            // configured strategy", which silently ran the whole bank regardless of what was requested.
            ActiveStrategyIds = spec.Strategies,
            MinLogLevel = LogLevel.Warning,
            PreloadedConfig = runConfig,
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
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
            TryDeleteDb(dbPath);
        }

        return (runTrades.AsReadOnly(), runEquity.AsReadOnly());
    }

    // iter-tape-enable Tier1: persist the run row with its real score right where it's computed, instead of
    // gating on a BacktestRunSummary lookup that could never succeed (RunSingleAsync's temp per-fold db is
    // deleted before AddRunAsync could see it, and nothing ever wrote a summary row into it in the first
    // place) — Experiment.Runs stayed permanently empty.
    private async Task PersistRunAsync(
        Guid experimentId, string variantLabel, string runId, FoldScore score, CancellationToken ct)
    {
        try
        {
            await _experimentRepo.AddRunAsync(new ExperimentRunEntity
            {
                Id = Guid.NewGuid(),
                ExperimentId = experimentId,
                BacktestRunId = runId,
                VariantLabel = variantLabel,
                FoldIndex = score.FoldIndex,
                FoldRole = score.FoldRole,
                ScoreJson = JsonSerializer.Serialize(score),
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist run record {RunId} for experiment {ExperimentId}", runId, experimentId);
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
