using TradingEngine.Engine;
using TradingEngine.Services;
using TradingEngine.Services.AddOns;

namespace TradingEngine.Host;

/// <summary>
/// The kernel-path adapter for per-bar trailing / breakeven (iter-36 K4 gap-3). It is the impure half of
/// the trailing seam: it reads the per-position management config + recent bars (which the pure kernel
/// cannot) and asks the real <see cref="IPositionManager"/> — the SAME tested component the imperative
/// <c>TradingLoop.UpdateTrailingStopsAsync</c> used — for stop moves. The kernel loop turns each move into
/// a <see cref="StopLossModifyRequested"/> event, which the reducer applies purely (update the
/// authoritative stop + emit a <see cref="ModifyStopLoss"/> effect to the venue).
///
/// Registration is lazy: a position is registered with the position manager the first bar it is seen Open
/// (carrying its strategy's <c>PositionManagement</c> config). Deregistration is handled by the
/// <c>EffectExecutor</c> on the position's <see cref="DeregisterRisk"/> effect, so this only ever adds.
/// </summary>
public sealed class KernelTrailingEvaluator(
    IPositionManager positionManager,
    ISymbolInfoRegistry symbolRegistry,
    IndicatorSnapshotService indicatorSnapshot,
    IReadOnlyList<IStrategy> strategies,
    AddOnResolver addOnResolver,
    IIndicatorService indicators)
{
    private readonly HashSet<Guid> _registered = [];

    public void Reset() => _registered.Clear();

    /// <summary>Evaluate trailing for every Open position on this bar's symbol; returns the stop moves
    /// (position id + new stop) the kernel should apply. Pure of kernel state — the only thing it mutates
    /// is the position manager's own per-position water-mark/config state (deterministic over the tape).</summary>
    public IReadOnlyList<(Guid PositionId, Price NewStopLoss)> Evaluate(Bar bar, EngineState state)
    {
        if (state.Positions.Count == 0) return [];

        var halfSpread = ResolveHalfSpread(bar.Symbol);
        var tick = new Tick(bar.Symbol, bar.Close, bar.Close + halfSpread, bar.OpenTimeUtc + GetBarDuration(bar.Timeframe));
        var recentBars = GetRecentBars(bar.Symbol, bar.Timeframe);

        List<(Guid, Price)>? moves = null;
        foreach (var (id, ps) in state.Positions)
        {
            if (ps.Phase != PositionPhase.Open || ps.Symbol != bar.Symbol) continue;

            var position = new Position(
                ps.PositionId, ps.OrderId, ps.Symbol, ps.Direction, ps.Lots,
                ps.EntryPrice, ps.CurrentStopLoss, ps.TakeProfit,
                ps.OpenedAtUtc == DateTime.MinValue ? bar.OpenTimeUtc : ps.OpenedAtUtc, ps.StrategyId);

            if (_registered.Add(position.Id))
            {
                var pmOptions = strategies.FirstOrDefault(s => s.Id == position.StrategyId)?.Config.PositionManagement
                    ?? new PositionManagementOptions();
                // iter-38 A3 / D2: resolve add-ons ONCE at entry (registration) and freeze them onto this
                // position's config. Auto-mode add-ons get tuner-derived numbers from this bar's volatility;
                // a position with no add-ons enabled resolves to an identical config (pass-through), so the
                // default/golden path stays byte-identical.
                var resolved = addOnResolver.ResolveAtEntry(pmOptions, bar.Timeframe, BuildVolatility(bar, recentBars)).Resolved;
                positionManager.RegisterPosition(position, PositionManager.BuildConfig(position.StrategyId, resolved, 0m));
            }

            foreach (var mod in positionManager.Evaluate(position, tick, recentBars))
            {
                if (mod is not MoveStopLoss move) continue;
                (moves ??= []).Add((id, move.NewStopLoss));
            }
        }

        return (IReadOnlyList<(Guid, Price)>?)moves ?? [];
    }

    /// <summary>iter-38 A3: the volatility context fed to the add-on tuner at entry. ATR (14) from this
    /// symbol/timeframe's recent bars, converted to pips; spread from the symbol registry. Unknown symbol or
    /// too-few bars ⇒ neutral (0 ⇒ the tuner uses a 1.0 volatility factor), so resolution never throws.</summary>
    private VolatilityContext BuildVolatility(Bar bar, IReadOnlyList<Bar> recentBars)
    {
        double atrPips = 0, spreadPips = 0;
        try
        {
            var info = symbolRegistry.Get(bar.Symbol);
            var pip = (double)info.PipSize;
            if (pip > 0)
            {
                spreadPips = (double)info.TypicalSpread / pip;
                if (recentBars.Count >= 2)
                    atrPips = indicators.Atr(recentBars, 14) / pip;
            }
        }
        catch { /* unknown symbol ⇒ neutral volatility */ }
        return new VolatilityContext(AtrPips: atrPips, TypicalSpreadPips: spreadPips, ReferenceAtrPips: 0);
    }

    private IReadOnlyList<Bar> GetRecentBars(Symbol symbol, Timeframe tf)
    {
        if (indicatorSnapshot.Bars.TryGetValue(symbol, out var byTf) && byTf.TryGetValue(tf, out var list))
            lock (list) return list.ToList();
        return [];
    }

    private decimal ResolveHalfSpread(Symbol symbol)
    {
        try { return symbolRegistry.Get(symbol).TypicalSpread / 2m; }
        catch { return 0.00005m; }
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
}
