using System.Text.Json;
using TradingEngine.CTraderRunner;
using TradingEngine.Domain;
using TradingEngine.Domain.Interfaces;
using TradingEngine.Infrastructure.Configuration;
using TradingEngine.Services;

namespace TradingEngine.Web.Services;

/// <summary>
/// Builds the engine's <see cref="LoadedConfig"/> for a run from the DATABASE (canonical config
/// source) rather than letting the inner host re-read config/strategies/*.json — plus the audit
/// EffectiveConfigJson stored on the run record. Extracted verbatim from BacktestOrchestrator;
/// every doctrine comment travels with the code it guards.
/// </summary>
public sealed class RunConfigAssembler(
    IServiceScopeFactory scopeFactory,
    EffectiveConfigResolver configResolver,
    ILogger<RunConfigAssembler> logger)
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly EffectiveConfigResolver _configResolver = configResolver;
    private readonly ILogger<RunConfigAssembler> _logger = logger;

    // Builds the engine's LoadedConfig from the DATABASE (canonical config source) rather than letting
    // the inner host re-read config/strategies/*.json. Strategy parameters, symbols, timeframe, regime
    // filter, order-entry and position-management all come from the seeded DB store, so what the New-
    // Backtest UI shows/edits is exactly what the engine evaluates. Risk profiles, prop-firm rules,
    // governor and sizing are also loaded from DB stores (seeded from JSON at startup).
    // iter-strategy-system P1: <paramref name="perPassPacks"/> drives per-row add-on packs (D3). When non-null
    // (the row-based builder), it is the strategy→packId map for ONE execution pass: each listed strategy is
    // force-enabled for the run (the user put it in a row, so a DB Enabled=false must not silently drop it)
    // and gets that row's pack — so the SAME strategy can carry DIFFERENT packs on different (symbol,tf) passes.
    // When null, the legacy global pack logic (UsePackId / PerStrategyPackIds) applies. The governor toggle
    // (D4) is honoured for both paths via CustomParams["GovernorEnabled"].
    public async Task<LoadedConfig> BuildLoadedConfigFromDbAsync(
        BacktestConfig cfg, IReadOnlyDictionary<string, string?>? perPassPacks = null)
    {
        var solutionRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var baseConfig = new ConfigLoader(solutionRoot).LoadBase();

        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IStrategyConfigStore>();
        var dbConfigs = await store.GetAllAsync(CancellationToken.None);

        // iter-redesign-ctrader P3.2: load DB risk profiles early so profileIsKnown checks BOTH the JSON
        // base config AND the DB store. Before this fix, only baseConfig.RiskProfiles was checked; a
        // "raw" profile seeded into the DB was invisible, so the strategy kept its stored (standard)
        // profile and the raw prop-firm toggles never loaded.
        var rpStore = scope.ServiceProvider.GetRequiredService<IRiskProfileStore>();
        var dbRiskProfiles = await rpStore.GetAllAsync(CancellationToken.None);
        var riskProfiles = dbRiskProfiles.Count > 0 ? dbRiskProfiles : baseConfig.RiskProfiles;

        var chosenProfile = cfg.CustomParams.GetValueOrDefault("RiskProfileId");
        var profileIsKnown = !string.IsNullOrWhiteSpace(chosenProfile)
            && riskProfiles.Any(r => r.Id == chosenProfile);

        var strategyConfigs = new List<StrategyConfigEntry>();
        {
            // iter-38 PK3 / D1: apply a named add-on pack over each strategy's own add-ons (per-strategy pack
            // wins over the global UsePackId; the pack REPLACES enrichments, baseline SL/TP stays — D4).
            var usePackId = cfg.CustomParams.GetValueOrDefault("UsePackId");
            var disableRegime = cfg.CustomParams.GetValueOrDefault("DisableRegime") == "true";   // iter-38 R1 run-master
            // iter-redesign P3.2 (D2): "no add-ons (raw)" mode — strip every add-on so the strategy runs its
            // baseline SL/TP only, with no breakeven/trailing/partial/ride/dynamic enrichment. Wins over any
            // pack so the owner can A/B raw vs add-on'd and watch the unmasked drawdown.
            var stripAddOns = cfg.CustomParams.GetValueOrDefault("StripAddOns") == "true";
            Dictionary<string, string>? perStrategyPacks = null;
            if (perPassPacks is null
                && cfg.CustomParams.TryGetValue("PerStrategyPackIds", out var ppJson) && !string.IsNullOrWhiteSpace(ppJson))
            {
                try { perStrategyPacks = JsonSerializer.Deserialize<Dictionary<string, string>>(ppJson); }
                catch (Exception ex) { _logger.LogWarning(ex, "Bad PerStrategyPackIds JSON — ignoring"); }
            }

            var packStore = scope.ServiceProvider.GetRequiredService<IAddOnPackStore>();
            var packCache = new Dictionary<string, AddOnPack?>();

            foreach (var c0 in dbConfigs)
            {
                var c = profileIsKnown ? c0 with { RiskProfileId = chosenProfile! } : c0;

                // iter-strategy-system P1 (D3): a row-selected strategy runs for this pass regardless of its
                // stored Enabled flag; routing to the right pass is the RunPlan's job (StrategyBankService).
                if (perPassPacks is not null && perPassPacks.ContainsKey(c.Id))
                    c = c with { Enabled = true };

                var packId = perPassPacks is not null
                    ? perPassPacks.GetValueOrDefault(c.Id)
                    : perStrategyPacks?.GetValueOrDefault(c.Id) ?? usePackId;
                if (!string.IsNullOrWhiteSpace(packId))
                {
                    if (!packCache.TryGetValue(packId, out var pack))
                    {
                        pack = await packStore.GetByIdAsync(packId, CancellationToken.None);
                        packCache[packId] = pack;
                    }
                    if (pack is not null)
                    {
                        c = c with {
                            PositionManagement = _configResolver.ApplyPack(c.PositionManagement, pack),
                            RegimeFilter = (c.RegimeFilter ?? new RegimeFilterOptions()) with {
                                DetectionEnabled = pack.RegimeDetectionEnabled
                            }
                        };
                    }
                }
                // iter-38 R1 run-master: force regime detection OFF for every strategy this run. The existing
                // per-strategy mechanism (RegimeFilterOptions.DetectionEnabled=false ⇒ Allows allow-all) then
                // lets the strategy trade in any regime — no engine-path change needed.
                if (disableRegime)
                    c = c with { RegimeFilter = (c.RegimeFilter ?? new RegimeFilterOptions()) with { DetectionEnabled = false } };

                // iter-redesign P3.2 (D2): strip add-ons last so it overrides both the strategy's stored
                // enrichments AND any applied pack — a "raw" run is provably free of breakeven/trailing/
                // partial/ride/dynamic-SL/TP (baseline SL/TP preserved).
                if (stripAddOns)
                    c = c with { PositionManagement = EffectiveConfigResolver.StripAddOns(c.PositionManagement) };

                // P3.2 exploration mode: after stripping add-ons (if requested), force every strategy
                // to the exploration preset — SL=ATR×4, TP=none, zero enrichments — so the entry signal
                // runs bare. The recorded excursion paths (RecordExcursions=true) are the raw measure of
                // entry quality that the P3.3 ExitReplayer calibrates exits from.
                if (cfg.CustomParams.GetValueOrDefault("ExplorationMode") == "true")
                    c = c with { PositionManagement = EffectiveConfigResolver.ApplyExplorationPreset(c.PositionManagement) };

                strategyConfigs.Add(c);
            }

            var runOverrides = RunRequestParser.ParseOverrides(cfg);
            if (runOverrides.Count > 0)
            {
                for (var i = 0; i < strategyConfigs.Count; i++)
                {
                    var c = strategyConfigs[i];
                    if (runOverrides.TryGetValue(c.Id, out var ovr))
                    {
                        var resolved = _configResolver.Resolve(c, ovr);
                        strategyConfigs[i] = c with
                        {
                            Parameters = resolved.Parameters,
                            PositionManagement = resolved.PositionManagement,
                            OrderEntry = resolved.OrderEntry,
                            RegimeFilter = resolved.RegimeFilter,
                            Reentry = resolved.Reentry,
                        };
                    }
                }
            }
        }

        var pfStore = scope.ServiceProvider.GetRequiredService<IPropFirmRuleSetStore>();
        var dbPropFirms = await pfStore.GetAllAsync(CancellationToken.None);
        var propFirms = dbPropFirms.Count > 0 ? dbPropFirms : baseConfig.PropFirms;

        GovernorOptions governor;
        try
        {
            var govStore = scope.ServiceProvider.GetRequiredService<IGovernorOptionsStore>();
            governor = await govStore.GetAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load governor options from DB — falling back to JSON config defaults (M19 fix)");
            governor = baseConfig.Governor;
        }

        // iter-strategy-system P1 (D4): run-level governor toggle. Default (absent/"true") keeps the stored
        // governor; "false" disables it for the whole run.
        if (cfg.CustomParams.GetValueOrDefault("GovernorEnabled") == "false")
            governor = governor with { Enabled = false };

        // iter-strategy-system P5: run-level protection toggle overrides. Default (absent/"true") keeps
        // the ruleset defaults. "false" forces the corresponding protection OFF by ANDing into every
        // ruleset's ProtectionToggles, regardless of which ruleset gets selected later.
        var perRunDailyDd = cfg.CustomParams.GetValueOrDefault("DailyDdEnabled") != "false";
        var perRunMaxDd = cfg.CustomParams.GetValueOrDefault("MaxDdEnabled") != "false";
        var perRunForceClose = cfg.CustomParams.GetValueOrDefault("ForceCloseOnBreachEnabled") != "false";
        // iter-redesign P2.2: exposure / daily-budget+heat / position-count limiters are now per-run
        // overridable too, so a "Raw" run can provably disable every limiter (not just the DD set).
        var perRunExposure = cfg.CustomParams.GetValueOrDefault("ExposureEnabled") != "false";
        var perRunBudget = cfg.CustomParams.GetValueOrDefault("BudgetEnabled") != "false";
        var perRunMaxPositions = cfg.CustomParams.GetValueOrDefault("MaxPositionsEnabled") != "false";
        if (!perRunDailyDd || !perRunMaxDd || !perRunForceClose
            || !perRunExposure || !perRunBudget || !perRunMaxPositions)
        {
            propFirms = propFirms.Select(pf => pf with
            {
                Toggles = pf.Toggles with
                {
                    DailyDdEnabled = pf.Toggles.DailyDdEnabled && perRunDailyDd,
                    MaxDdEnabled = pf.Toggles.MaxDdEnabled && perRunMaxDd,
                    ForceCloseOnBreachEnabled = pf.Toggles.ForceCloseOnBreachEnabled && perRunForceClose,
                    ExposureEnabled = pf.Toggles.ExposureEnabled && perRunExposure,
                    BudgetEnabled = pf.Toggles.BudgetEnabled && perRunBudget,
                    MaxPositionsEnabled = pf.Toggles.MaxPositionsEnabled && perRunMaxPositions,
                }
            }).ToList();
        }

        return new LoadedConfig(propFirms, riskProfiles)
        {
            StrategyConfigs = strategyConfigs,
            NewsWindows = baseConfig.NewsWindows,
            StrategyRotation = baseConfig.StrategyRotation,
            Governor = governor,
            SizingPolicy = baseConfig.SizingPolicy,
            Regime = baseConfig.Regime,
        };
    }

    public async Task<string?> ResolveEffectiveConfigJsonAsync(BacktestConfig cfg)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IStrategyConfigStore>();
            var storedConfigs = await store.GetAllAsync(CancellationToken.None);

            // iter-redesign-ctrader P3.1: include the resolved risk profile name in the audit config
            // so the stored EffectiveConfigJson reflects what actually ran — not just strategy overrides.
            var chosenProfile = cfg.CustomParams.GetValueOrDefault("RiskProfileId");
            var profileIsKnown = !string.IsNullOrWhiteSpace(chosenProfile)
                && (await scope.ServiceProvider.GetRequiredService<IRiskProfileStore>()
                    .GetAllAsync(CancellationToken.None) is { Count: > 0 } dbProf
                    ? dbProf
                    : new ConfigLoader(Path.GetFullPath(Path.Combine(
                        AppContext.BaseDirectory, "..", "..", "..", "..", ".."))).LoadBase().RiskProfiles)
                .Any(r => r.Id == chosenProfile);

            var overrides = RunRequestParser.ParseOverrides(cfg);
            var resolvedEntries = new List<EffectiveConfigEntry>();

            var strategyIds = RunRequestParser.ParseStrategyIds(cfg);
            if (strategyIds.Length == 0)
                strategyIds = storedConfigs.Where(s => s.Enabled).Select(s => s.Id).ToArray();

            foreach (var sid in strategyIds)
            {
                var stored = storedConfigs.FirstOrDefault(s => s.Id == sid);
                if (stored is null) continue;
                var ovr = overrides.GetValueOrDefault(sid);

                // Stamp the chosen risk profile onto the stored config so the audit JSON reflects it.
                var stamped = profileIsKnown ? stored with { RiskProfileId = chosenProfile! } : stored;
                resolvedEntries.Add(_configResolver.Resolve(stamped, ovr));
            }

            if (resolvedEntries.Count == 0) return null;

            return JsonSerializer.Serialize(resolvedEntries, new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve effective config for run {RunId}", cfg.RunId);
            return null;
        }
    }
}
