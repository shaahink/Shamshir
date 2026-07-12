using Microsoft.Extensions.Logging;

namespace TradingEngine.Domain;

public sealed record EngineHostOptions
{
    public required string RunId { get; init; }
    public required EngineMode Mode { get; init; }
    public required Func<IServiceProvider, IBrokerAdapter> AdapterFactory { get; init; }
    public required string DbPath { get; init; }
    public required string SolutionRoot { get; init; }
    public IReadOnlyList<string> SymbolNames { get; init; } = [];

    /// <summary>
    /// Strategy IDs selected for this run. Empty = all configured strategies (the default).
    /// When non-empty, only configured strategies whose ID is in this set are instantiated, so a
    /// backtest honours the strategy picker on the New-Backtest page instead of always running all.
    /// </summary>
    public IReadOnlyList<string> ActiveStrategyIds { get; init; } = [];

    /// <summary>
    /// Overrides the strategy's built-in symbols/timeframe. When provided,
    /// <see cref="IStrategyBank.GetActive"/> filters strategies by whether the
    /// run plan includes the (strategyId, symbol, timeframe) combination.
    /// When null, falls back to the stored config's symbols/timeframe.
    /// </summary>
    public RunPlan? RunPlan { get; init; }

    public IProgress<BacktestProgressEvent>? Progress { get; init; }
    public LogLevel MinLogLevel { get; init; } = LogLevel.Information;
    public LoadedConfig? PreloadedConfig { get; init; }
    public bool DiagnosticsEnabled { get; init; }

    /// <summary>
    /// P0.1 (F1): the run's configured account balance. In backtest this is the sizing authority; the
    /// venue-reported hello balance must never override it. 0 = not set (older callers fall back to the
    /// venue/RiskManager balance, preserving pre-P0.1 behaviour).
    /// </summary>
    public decimal InitialBalance { get; init; }

    /// <summary>
    /// The currency the account is denominated in (F34). This is the ONE place the denomination is named:
    /// it stamps every <see cref="SymbolInfo.AccountCurrency"/>, drives which cross-rate legs the run must
    /// source, and is checked against the currency the venue declares — a mismatch fails the run rather
    /// than silently scaling every money figure by an FX rate. Switching the live account to GBP is this
    /// value plus the GBPUSD data the feed already loads.
    /// </summary>
    public string AccountCurrency { get; init; } = "USD";

    /// <summary>
    /// Pre-loaded USD-leg rate series (currency → observations), built by the caller that owns market data.
    /// Supplies the legs a run needs but never streams — the account currency itself, and crosses like
    /// EURJPY whose USDJPY leg is not traded. Null in hosts with no market data (unit harnesses), which
    /// then rely on streamed bars alone.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<CrossRatePoint>>? CrossRateSeries { get; init; }

    public IRunDataCache? RunDataCache { get; init; }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<Timeframe, IReadOnlyList<Bar>>>? PreloadedAuxBars { get; init; }

    public bool SkipJournal { get; init; }
}
