namespace TradingEngine.Web.Dtos.Runs;

public sealed record StartRunRequest
{
    public string Symbol { get; init; } = "EURUSD";
    public string Period { get; init; } = "h1";
    public DateTime Start { get; init; } = new(2024, 1, 1);
    public DateTime End { get; init; } = new(2024, 1, 31);
    public decimal Balance { get; init; } = 100_000;
    public double CommissionPerMillion { get; init; } = 30;
    public double SpreadPips { get; init; } = 1;
    public List<string>? Symbols { get; init; }
    public List<string>? Periods { get; init; }
    public List<string>? StrategyIds { get; init; }

    /// <summary>Risk profile id chosen for this run (applied to every strategy; overrides the stored
    /// per-strategy profile). Must match a configured/seeded risk profile.</summary>
    public string? RiskProfileId { get; init; }

    /// <summary>Data venue: "ctrader" (stream bars over NetMQ) or "replay" (credential-free, from
    /// stored bars). Absent = the configured default (CTrader:UseForBacktest).</summary>
    public string? Venue { get; init; }

    /// <summary>Per-strategy parameter overrides keyed by strategy id (H24). Propagated through
    /// <c>EffectiveConfigResolver</c> into the run's ConfigSet.</summary>
    public Dictionary<string, Dictionary<string, object>>? StrategyOverrides { get; init; }

    /// <summary>iter-38 (PK3 / D1). Reusable add-on pack applied to every strategy this run (the pack REPLACES
    /// each strategy's own add-ons; baseline SL/TP stays). Absent ⇒ each strategy uses its own add-ons.</summary>
    public string? UsePackId { get; init; }

    /// <summary>iter-38 (PK3 / D1). Per-strategy pack override, keyed by strategy id. Takes precedence over
    /// <see cref="UsePackId"/> for the named strategies.</summary>
    public Dictionary<string, string>? PerStrategyPackIds { get; init; }

    /// <summary>iter-38 (R1 / D3). Run-level master switch: when true, regime detection is forced OFF for every
    /// strategy this run (overrides each strategy's own regime-detection flag).</summary>
    public bool DisableRegime { get; init; }
}
