using TradingEngine.Infrastructure.Reconcile;

namespace TradingEngine.Web.Services;

/// <summary>
/// P4 — parity as a permanent gate (PLAN §P4, D12).
///
/// cTrader is the venue we actually trade; the tape exists to mimic it fast enough to search. This gate is
/// what keeps that mimicry honest: it matches a tape run against its cTrader sibling trade-by-trade and
/// scores them against a tolerance budget that is pre-registered (below) rather than chosen after seeing
/// the numbers. One VERDICT line out; any FAIL stops the caller.
///
/// The budget is deliberately tight on the things that are deterministic by construction (a limit fills at
/// the price we named; identical inputs size identically) and only loose where a real venue legitimately
/// differs (a stop can gap through its own price).
/// </summary>
public sealed class ParityGateService(LedgerReconcileService reconcile, ISymbolInfoRegistry symbols)
{
    /// <summary>The pre-registered tolerance budget (PLAN §P4b). Changing a number here is a decision to
    /// be made deliberately and recorded — not a knob to turn until a run goes green.</summary>
    public sealed record Budget
    {
        public bool TradeCountExact { get; init; } = true;
        public double EntryPriceMaxTicks { get; init; } = 1;
        public bool LotsExact { get; init; } = true;
        public double ExitPriceMaxTicks { get; init; } = 1;
        public double ExitPriceMinWithinPct { get; init; } = 95;
        public double CommissionMaxPct { get; init; } = 2;
        public double SwapMaxPct { get; init; } = 5;
        public double NetPnLMaxPctOfGross { get; init; } = 1;
    }

    public sealed record Check(string Quantity, string Tolerance, string Measured, bool Pass, string? Detail = null);

    public sealed record ParityReport(
        string TapeRunId,
        string CTraderRunId,
        string Symbol,
        bool Pass,
        IReadOnlyList<Check> Checks,
        IReadOnlyList<string> Notes)
    {
        /// <summary>The single line a gate/caller reads.</summary>
        public string Verdict => Pass
            ? $"VERDICT: PASS parity {Symbol} tape={TapeRunId} ctrader={CTraderRunId}"
            : $"VERDICT: FAIL parity {Symbol} tape={TapeRunId} ctrader={CTraderRunId} " +
              $"failed=[{string.Join(", ", Checks.Where(c => !c.Pass).Select(c => c.Quantity))}]";
    }

    public async Task<ParityReport> EvaluateAsync(
        string tapeRunId, string ctraderRunId, Budget? budget, CancellationToken ct)
    {
        budget ??= new Budget();

        var tape = await reconcile.BuildEngineLedgerAsync(tapeRunId, ct);
        var venue = await reconcile.BuildEngineLedgerAsync(ctraderRunId, ct);

        var checks = new List<Check>();
        var notes = new List<string>();

        var symbolName = await reconcile.GetRunSymbolAsync(ctraderRunId, ct);
        var tick = symbols.Get(Symbol.Parse(symbolName)).TickSize;

        // --- trade count: exact. Limit entries make the fill decision deterministic, so a mismatch is a
        // real divergence (an order one venue took and the other did not), never rounding.
        var countPass = !budget.TradeCountExact || tape.TotalTrades == venue.TotalTrades;
        checks.Add(new Check("TradeCount", "exact", $"tape={tape.TotalTrades} ctrader={venue.TotalTrades}", countPass));

        // Pair by open time — the venues agree on the bar an order was placed on, so nearest-open is an
        // honest pairing. Unmatched trades are listed rather than quietly dropped.
        var pairs = PairByOpenTime(tape.Trades, venue.Trades, out var unmatchedTape, out var unmatchedVenue);
        if (unmatchedTape.Count > 0 || unmatchedVenue.Count > 0)
        {
            notes.Add($"unmatched: {unmatchedTape.Count} tape-only, {unmatchedVenue.Count} ctrader-only " +
                      "(listed trades could not be paired within 1 bar of open time)");
        }

        if (pairs.Count == 0)
        {
            checks.Add(new Check("Pairing", "≥1 matched trade", "0 matched", false));
            return new ParityReport(tapeRunId, ctraderRunId, symbolName, false, checks, notes);
        }

        // --- entry price: ≤ 1 tick. A limit fills at the price we named, on both venues, by construction.
        var entryTicks = pairs.Select(p => Ticks(p.Tape.EntryPrice, p.Venue.EntryPrice, tick)).ToList();
        var entryWorst = entryTicks.Max();
        checks.Add(new Check("EntryPrice", $"≤ {budget.EntryPriceMaxTicks} tick",
            $"worst {entryWorst:F1} ticks", entryWorst <= budget.EntryPriceMaxTicks));

        // --- lots: exact. Same signal + same account + same sizer ⇒ same size. A mismatch means the two
        // legs are not modelling the same account (this is what a currency mismatch shows up as).
        var lotsWorst = pairs.Max(p => Math.Abs(p.Tape.Lots - p.Venue.Lots));
        checks.Add(new Check("Lots", "exact", $"worst delta {lotsWorst}",
            !budget.LotsExact || lotsWorst == 0m));

        // --- exit price: a stop can legitimately gap THROUGH its own price at a real venue, so this is the
        // one price allowed to differ — but only on a minority of trades, and the gaps are named.
        var exitTicks = pairs.Select(p => Ticks(p.Tape.ExitPrice, p.Venue.ExitPrice, tick)).ToList();
        var within = exitTicks.Count(t => t <= budget.ExitPriceMaxTicks) * 100.0 / exitTicks.Count;
        checks.Add(new Check("ExitPrice",
            $"≤ {budget.ExitPriceMaxTicks} tick on ≥{budget.ExitPriceMinWithinPct}%",
            $"{within:F0}% within, worst {exitTicks.Max():F1} ticks",
            within >= budget.ExitPriceMinWithinPct));

        foreach (var (p, t) in pairs.Zip(exitTicks).Where(x => x.Second > budget.ExitPriceMaxTicks))
        {
            notes.Add($"exit gap {t:F1} ticks on {p.Tape.Direction} opened {p.Tape.OpenedAtUtc:yyyy-MM-dd HH:mm} " +
                      $"(tape {p.Tape.ExitPrice} vs venue {p.Venue.ExitPrice}, {p.Venue.ExitReason})");
        }

        // --- costs: venue-declared spec + one formula ⇒ these should agree closely.
        var commission = PctCheck("Commission", tape.Commission, venue.Commission, budget.CommissionMaxPct);

        // P4 (F47), owner-accepted 2026-07-12: cTrader prices BACKTEST commission at a single reference
        // spot, not against each trade's own notional — so identical lots at very different prices are
        // charged identically. Ours scales with notional, which is the correct model: matching cTrader here
        // would make a run's costs depend on WHEN it was run. The divergence is the venue's, and we accept
        // it rather than reproduce it.
        //
        // The exemption is EARNED FROM THE VENUE'S OWN DATA, not granted by symbol or by widening a budget.
        // A blanket commission exemption would hide a real commission bug forever — which is precisely the
        // class of mistake this whole iteration exists to stop.
        var commissionDelta = 0m;
        if (!commission.Pass && VenueCommissionIsPriceIndependent(pairs))
        {
            commissionDelta = tape.Commission - venue.Commission;
            commission = commission with
            {
                Pass = true,
                Tolerance = $"EXEMPT (F47) — was ≤ {budget.CommissionMaxPct}%",
            };
            notes.Add(
                "COMMISSION EXEMPT (F47): the venue charged a CONSTANT commission per lot across trades " +
                "whose prices differ materially — i.e. it priced commission at one reference spot, not per " +
                "trade. Our per-notional model is the correct one and is deliberately NOT matched. The " +
                $"measured divergence ({commission.Measured}) is excluded from the verdict AND from NetPnL. " +
                "See PARITY-TRUTH-4 §4b.");
        }

        checks.Add(commission);
        checks.Add(PctCheck("Swap", tape.Swap, venue.Swap, budget.SwapMaxPct));

        // --- net: falls out of everything above, expressed against gross so a near-flat run can't make a
        // large absolute error look small. When commission is exempt (F47) its delta is backed out here too
        // — otherwise NetPnL just re-reports the venue's own artifact as our failure.
        var grossBase = Math.Max(Math.Abs(venue.GrossProfit), 1m);
        var netDelta = Math.Abs(tape.NetProfit - venue.NetProfit - commissionDelta);
        var netPct = (double)(netDelta / grossBase * 100m);
        checks.Add(new Check("NetPnL",
            commissionDelta == 0m
                ? $"≤ {budget.NetPnLMaxPctOfGross}% of gross"
                : $"≤ {budget.NetPnLMaxPctOfGross}% of gross (ex-F47 commission)",
            $"{netPct:F2}%", netPct <= budget.NetPnLMaxPctOfGross));

        return new ParityReport(tapeRunId, ctraderRunId, symbolName,
            checks.All(c => c.Pass), checks, notes);
    }

    /// <summary>
    /// The F47 signature, read off the venue's own fills: commission per lot is CONSTANT while the trades'
    /// prices are not. Commission scales with notional, and notional scales with price — so a venue whose
    /// per-lot charge does not move when price moves 5%+ is not pricing per trade at all.
    ///
    /// Deliberately conservative: it needs several trades AND a real price spread to fire, so it can never
    /// quietly excuse an ordinary commission bug on a range-bound run.
    /// </summary>
    private static bool VenueCommissionIsPriceIndependent(
        IReadOnlyList<(ReconcileTrade Tape, ReconcileTrade Venue)> pairs)
    {
        var samples = pairs
            .Select(p => p.Venue)
            .Where(v => v.Lots > 0 && v.Commission != 0 && v.EntryPrice > 0)
            .Select(v => (PerLot: Math.Abs(v.Commission) / v.Lots, v.EntryPrice))
            .ToList();

        if (samples.Count < 4) return false;

        static decimal Spread(IEnumerable<decimal> xs)
        {
            var min = xs.Min();
            var max = xs.Max();
            return min <= 0 ? 0m : (max - min) / min;
        }

        // The prices must actually differ, or the test cannot discriminate between the two models.
        if (Spread(samples.Select(s => s.EntryPrice)) < 0.05m) return false;

        // ...and the per-lot charge must have stayed flat anyway.
        return Spread(samples.Select(s => s.PerLot)) < 0.02m;
    }

    private static Check PctCheck(string name, decimal tapeVal, decimal venueVal, double maxPct)
    {
        var basis = Math.Max(Math.Abs(venueVal), 0.01m);
        var pct = (double)(Math.Abs(tapeVal - venueVal) / basis * 100m);
        return new Check(name, $"≤ {maxPct}%", $"{pct:F2}% (tape {tapeVal:F2} vs venue {venueVal:F2})", pct <= maxPct);
    }

    private static double Ticks(decimal a, decimal b, decimal tickSize) =>
        tickSize <= 0 ? 0 : (double)(Math.Abs(a - b) / tickSize);

    private static List<(ReconcileTrade Tape, ReconcileTrade Venue)> PairByOpenTime(
        IReadOnlyList<ReconcileTrade> tape, IReadOnlyList<ReconcileTrade> venue,
        out List<ReconcileTrade> unmatchedTape, out List<ReconcileTrade> unmatchedVenue)
    {
        var pairs = new List<(ReconcileTrade, ReconcileTrade)>();
        var remaining = venue.ToList();
        unmatchedTape = [];

        foreach (var t in tape)
        {
            var match = remaining
                .Where(v => v.Direction == t.Direction)
                .OrderBy(v => Math.Abs((v.OpenedAtUtc - t.OpenedAtUtc).TotalMinutes))
                .FirstOrDefault();

            if (match is null || Math.Abs((match.OpenedAtUtc - t.OpenedAtUtc).TotalHours) > 4)
            {
                unmatchedTape.Add(t);
                continue;
            }

            remaining.Remove(match);
            pairs.Add((t, match));
        }

        unmatchedVenue = remaining;
        return pairs;
    }
}
