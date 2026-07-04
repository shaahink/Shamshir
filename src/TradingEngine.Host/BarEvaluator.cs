namespace TradingEngine.Host;

/// <summary>
/// The evaluator stage (iter-36 K1). The strategy/indicator evaluation that today lives imperatively in
/// <see cref="TradingLoop.ProcessBarAsync"/> becomes a deterministic <b>event producer</b> feeding the
/// kernel queue: per <see cref="BarClosed"/> it runs indicators → regime → active strategies → signal
/// gate, and for each firing strategy emits an <see cref="OrderProposed"/> event (carrying the
/// <c>SlPips</c> + cross-rate-aware <c>PipValuePerLot</c> the pure gate needs) plus a per-strategy
/// <see cref="StrategyVerdict"/> ("why" — signal/none + reason + indicators) for the journal/monitor.
///
/// It does NOT decide or size — that is the kernel (<c>PreTradeGate</c>). It only does what the kernel
/// cannot: read market context + indicators, and freeze the impure gate verdicts
/// (<see cref="ExternalVerdicts"/> — news/weekend/compliance/governor, ported from
/// <c>KernelOrderGate.ComputeVerdicts</c>) onto the proposal AT SIM-TIME, so the pure kernel applies them
/// deterministically and a replay reproduces the same accept/reject bit-for-bit (no date-dependence —
/// the latent bug the K0 note flagged). This is the K1 seam the kernel backtest loop (K3) wires up.
/// </summary>
public sealed class BarEvaluator(
    IndicatorSnapshotService indicatorSnapshot,
    IStrategyBank strategyBank,
    IRegimeDetector regimeDetector,
    ISignalGate? signalGate,
    EntryPlanner entryPlanner,
    ISymbolInfoRegistry symbolRegistry,
    Func<string, string, decimal> getCrossRate,
    INewsFilter newsFilter,
    SessionFilter sessionFilter,
    IRiskManager riskManager,
    IRiskProfileResolver riskProfileResolver,
    ITradingGovernor? governor,
    Microsoft.Extensions.Logging.ILogger logger,
    IIndicatorService indicators)
{
    private long _orderSeq;

    /// <summary>The last bar's evaluation, exposed so the driver's <c>evaluatorView</c> seam can fold the
    /// per-strategy verdicts + regime onto the BarClosed StepRecord (wired in K3/K5).</summary>
    public BarEvaluation Latest { get; private set; } = BarEvaluation.Empty;

    public void Reset()
    {
        Interlocked.Exchange(ref _orderSeq, 0);
        Latest = BarEvaluation.Empty;
    }

    /// <summary>
    /// Evaluate one closed bar against the current kernel state, producing the proposals to enqueue and
    /// the per-strategy verdicts. Pure of side effects on the kernel — the only state it mutates is its
    /// own indicator buffer (the incremental indicator window) and the deterministic order-id counter.
    /// </summary>
    public async Task<BarEvaluation> EvaluateAsync(BarClosed bar, EngineState state, CancellationToken ct)
    {
        var symbol = bar.Symbol;
        var tf = bar.Timeframe;
        var simTime = bar.BarOpenTimeUtc;

        // Maintain the incremental indicator window exactly as TradingLoop does.
        var byTf = indicatorSnapshot.Bars.GetOrAdd(symbol, _ => new());
        var list = byTf.GetOrAdd(tf, _ => new());
        var barModel = new Bar(symbol, tf, simTime, bar.Open, bar.High, bar.Low, bar.Close, 0);
        lock (list)
        {
            list.Add(barModel);
            while (list.Count > 500) list.RemoveAt(0);
        }

        await indicatorSnapshot.RecomputeIndicatorsAsync(symbol, tf, ct);

        var halfSpread = ResolveHalfSpread(symbol);
        var closeTick = new Tick(symbol, bar.Close, bar.Close + halfSpread, simTime + GetBarDuration(tf));
        var barSnapshot = indicatorSnapshot.BuildBarSnapshot(symbol);
        if (barSnapshot is null)
        {
            return Latest = BarEvaluation.Empty;
        }

        indicatorSnapshot.BuildSharedIndicatorSnapshot(symbol);

        // Sim-time anchored — these advance cooling-off / re-entry counters off the bar clock, never wall-clock.
        signalGate?.OnBar(simTime);
        governor?.OnBar(simTime);

        // iter-38 R1 / D3: regime detection is governed entirely by each strategy's
        // RegimeFilterOptions.DetectionEnabled — the single source of truth. A run forces it off for every
        // strategy via the run-master DisableRegime flag, which the orchestrator folds onto each strategy's
        // RegimeFilter.DetectionEnabled (a pack's RegimeDetectionEnabled and the per-strategy default fold the
        // same way). StrategyBankService.GetActive already lets a detection-off strategy trade in ANY regime
        // (its RegimeFilter.Allows short-circuits to allow-all), so the filtering is correct without a second
        // run-level switch here, and the golden path (all strategies detect-on) stays byte-identical.
        var regime = regimeDetector.Detect(symbol, barSnapshot[tf], indicatorSnapshot.ReusableIndicatorDict);
        var activeStrategies = strategyBank.GetActive(symbol, tf, regime);
        var regimeLabel = activeStrategies.Count > 0 && activeStrategies.All(s => !s.Config.RegimeFilter.DetectionEnabled)
            ? "Bypassed"
            : regime.ToString();

        var proposals = new List<OrderProposed>();
        var verdicts = new List<StrategyVerdict>();
        int totalBars = 0;

        foreach (var strategy in activeStrategies)
        {
            if (totalBars == 0)
                totalBars = barSnapshot.Values.Sum(b => b.Count);
            var strategyIndicators = indicatorSnapshot.BuildStrategyIndicatorValues(symbol, strategy);

            if (totalBars < strategy.RequiredBarCount)
            {
                verdicts.Add(new StrategyVerdict(strategy.Id, HadEnoughBars: false, SignalFired: false,
                    Direction: null, Reason: $"not enough bars (have {totalBars}, need {strategy.RequiredBarCount})",
                    Indicators: null));
                continue;
            }

            var context = new MarketContext(symbol, closeTick, barSnapshot, strategyIndicators, simTime);
            var intent = strategy.Evaluate(context);

            if (intent is null)
            {
                verdicts.Add(new StrategyVerdict(strategy.Id, HadEnoughBars: true, SignalFired: false,
                    Direction: null, Reason: "no signal", Indicators: null));
                continue;
            }

            intent = entryPlanner.Plan(intent, strategy.Config.OrderEntry, closeTick.Mid);

            if (signalGate is not null)
            {
                var gateResult = signalGate.Check(strategy.Id, intent.Symbol.Value, intent.Direction, simTime);
                if (!gateResult.Allowed)
                {
                    verdicts.Add(new StrategyVerdict(strategy.Id, HadEnoughBars: true, SignalFired: false,
                        Direction: intent.Direction, Reason: $"REENTRY:{gateResult.Reason}", Indicators: null));
                    continue;
                }
            }

            // The gate sizes the order; the evaluator supplies the market-context inputs it can't compute:
            // the stop distance in pips and the cross-rate-aware pip value (same path KernelOrderGate /
            // MapOpenPositionsToProjected use today, so non-account-currency symbols are correct).
            var symbolInfo = symbolRegistry.Get(intent.Symbol);
            var entryPrice = intent.LimitPrice ?? new Price(bar.Close);

            // iter-38 A6 / D4: the DynamicSlTp add-on REPLACES the strategy's baseline SL/TP with an
            // auto-tuned (or Custom) ATR stop + RR target. Off (null/Enabled=false) ⇒ the strategy's own
            // SL/TP stand, so the default/golden path is byte-identical.
            var pm = strategy.Config.PositionManagement;
            if (pm.DynamicSlTp is { Enabled: true } dyn)
            {
                var atrPrice = indicators.Atr(barSnapshot[tf], 14);
                if (atrPrice > 0)
                {
                    double slMult, tpRr;
                    if (dyn.Mode == AddOnMode.Auto)
                    {
                        var pip = (double)symbolInfo.PipSize;
                        var vol = new TradingEngine.Services.AddOns.VolatilityContext(
                            AtrPips: pip > 0 ? atrPrice / pip : 0,
                            TypicalSpreadPips: pip > 0 ? (double)symbolInfo.TypicalSpread / pip : 0,
                            ReferenceAtrPips: TradingEngine.Services.AddOns.AddOnAutoTuner.ReferenceAtrPips(
                                tf, pip > 0 ? (double)symbolInfo.TypicalSpread / pip : 0));
                        var tuned = TradingEngine.Services.AddOns.AddOnAutoTuner.Tune(tf, vol);
                        slMult = tuned.DynamicSlAtrMultiple;
                        tpRr = tuned.DynamicTpRrMultiple;
                    }
                    else
                    {
                        slMult = dyn.AtrMultipleSl;
                        tpRr = dyn.RrMultipleTp;
                    }

                    var dynSl = SlTpHelpers.AtrBased(entryPrice, intent.Direction, atrPrice, slMult, symbolInfo);
                    var dynTp = SlTpHelpers.RRMultiple(entryPrice, dynSl, intent.Direction, tpRr, symbolInfo);
                    intent = intent with { StopLoss = dynSl, TakeProfit = dynTp };
                }
            }

            var slPips = (decimal)PipCalculator.Distance(entryPrice, intent.StopLoss, symbolInfo).Value;
            var pipValuePerLot = PipCalculator.PipValuePerLot(symbolInfo, entryPrice.Value, getCrossRate);

            // K4 gap-1: resolve the strategy's profile ONCE here and carry it on the proposal so the pure
            // kernel sizes with it (not the run-constant profile). Reused for the compliance verdict below.
            var resolvedProfile = riskProfileResolver.Resolve(intent.RiskProfileId);

            var external = ComputeVerdicts(intent, state, simTime, resolvedProfile);

            var proposal = new OrderProposed(
                DeterministicOrderId(Interlocked.Increment(ref _orderSeq)),
                intent.Symbol, intent.Direction, intent.OrderType,
                intent.LimitPrice, intent.StopLoss, intent.TakeProfit, intent.StrategyId,
                entryPrice.Value, slPips, pipValuePerLot, simTime, external, resolvedProfile,
                EntryReason: intent.Reason, EntryRegime: regimeLabel, EntryTimeframe: strategy.EntryTimeframe);

            proposals.Add(proposal);
            verdicts.Add(new StrategyVerdict(strategy.Id, HadEnoughBars: true, SignalFired: true,
                Direction: intent.Direction, Reason: intent.Reason, Indicators: strategyIndicators));

            logger.LogInformation("PROPOSE|{Strategy}|{Symbol}|{Dir}|slPips={SlPips:F1}|entry={Entry:F5}",
                strategy.Id, symbol.Value, intent.Direction, slPips, entryPrice.Value);
        }

        return Latest = new BarEvaluation(regimeLabel, proposals, verdicts);
    }

    /// <summary>
    /// Port of <c>KernelOrderGate.ComputeVerdicts</c> — the service-dependent gate verdicts the pure kernel
    /// cannot compute (news window / weekend close / prop-firm compliance / legacy governor). Evaluated at
    /// the BAR's sim-time (not wall-clock), folding in the rule-set toggles, so the verdict is frozen onto
    /// the proposal and replay-stable.
    /// </summary>
    private ExternalVerdicts ComputeVerdicts(TradeIntent intent, EngineState state, DateTime simTime, RiskProfile profile)
    {
        var ruleSet = riskManager.ActiveRuleSet;
        var newsActive = ruleSet?.AllowTradesDuringNews == false && newsFilter.IsNewsWindowActive(intent.Symbol, simTime);
        var weekend = sessionFilter.IsWeekend(simTime) && ruleSet?.AllowWeekendHolding == false;

        var compliance = riskManager.CheckComplianceBlock(intent, profile);

        string? governorReason = null;
        if (governor is not null)
        {
            var dayStart = riskManager.Drawdown.DailyStartEquity;
            var equity = state.Account.Equity;
            var dayPnLFraction = dayStart > 0 ? (equity - dayStart) / dayStart : 0m;
            var ctx = new GovernorContext(dayPnLFraction, dayStart, equity, 0, ruleSet ?? DefaultRuleSet());
            var decision = governor.Evaluate(ctx);
            if (!decision.AllowNewTrades)
            {
                governorReason = decision.Reason;
            }
        }

        return new ExternalVerdicts(newsActive, weekend, compliance, governorReason);
    }

    // Deterministic, replay-stable order id: a per-run monotonic counter encoded into a Guid. No
    // Guid.NewGuid (which would make the journal differ across identical runs). The evaluator is impure
    // (outside the kernel) but stays deterministic so the same tape ⇒ the same proposal ids.
    private static Guid DeterministicOrderId(long seq)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, seq);
        return new Guid(bytes);
    }

    private decimal ResolveHalfSpread(Symbol symbol)
    {
        try { return symbolRegistry.Get(symbol).TypicalSpread / 2m; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ResolveHalfSpread failed for {Symbol} — using fallback 0.5pip", symbol);
            return 0.00005m;
        }
    }

    private static TimeSpan GetBarDuration(Timeframe tf) => tf switch
    {
        Timeframe.M1 => TimeSpan.FromMinutes(1),
        Timeframe.M5 => TimeSpan.FromMinutes(5),
        Timeframe.M15 => TimeSpan.FromMinutes(15),
        Timeframe.M30 => TimeSpan.FromMinutes(30),
        Timeframe.H1 => TimeSpan.FromHours(1),
        Timeframe.H4 => TimeSpan.FromHours(4),
        Timeframe.D1 => TimeSpan.FromDays(1),
        _ => TimeSpan.FromHours(1),
    };

    private static PropFirmRuleSet DefaultRuleSet() => new(
        "none", "None", "Fixed", 0.05, 0.10, 0.10, 0,
        "BalancePlusFloating", "22:00:00", "UTC", false, "High", 0, 0,
        false, "21:00:00", "20:00:00", "NextTradingDay", false);
}

/// <summary>
/// The result of evaluating one bar: the regime label, the <see cref="OrderProposed"/> events to enqueue
/// (one per firing strategy), and the per-strategy verdicts (the "why" for the journal).
/// </summary>
public sealed record BarEvaluation(
    string? Regime,
    IReadOnlyList<OrderProposed> Proposals,
    IReadOnlyList<StrategyVerdict> Verdicts)
{
    public static readonly BarEvaluation Empty = new(null, [], []);
}
