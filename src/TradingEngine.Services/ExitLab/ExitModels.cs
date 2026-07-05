namespace TradingEngine.Services.ExitLab;

// P3.3: the excursion path format stored by TapeReplayAdapter (P3.1). HiPips/LoPips are signed
// distances from entry price in pips (positive = above entry, negative = below entry). Bar-level
// granularity — the replayer walks this point-by-point detecting SL/TP/BE/Trail hits.
public readonly record struct ExcursionPoint(int MinutesSinceEntry, double HiPips, double LoPips);

public sealed record TradeExcursionInput
{
    public required TradeDirection Direction { get; init; }
    public required decimal EntryPrice { get; init; }
    public required Price InitialStopLoss { get; init; }
    public required decimal PipSize { get; init; }
    public required double SpreadPips { get; init; }
    public required IReadOnlyList<ExcursionPoint> Path { get; init; }
}

public sealed record ExitRule
{
    // Baseline SL — the stop distance floor that BE/trail never improves beyond.
    public double SlAtrMultiple { get; init; } = 1.5;
    public double? TpRrMultiple { get; init; }  // null = no TP (rides to end-of-data or flatten)

    // Add-ons (all nullable = disabled).
    public double? BeTriggerR { get; init; }     // null = no breakeven
    public double? BeOffsetPips { get; init; }   // buffer beyond entry after BE triggers (default 0)
    public double? TrailAtrMultiple { get; init; } // null = no trailing
    public double? PartialTriggerR { get; init; }
    public double? PartialCloseFraction { get; init; } // 0.25 or 0.5

    // The ATR reference (pips) for converting multiples to distance. The caller provides this
    // per-symbol×TF so the replayer stays pure — it doesn't look up the symbol table.
    public double ReferenceAtrPips { get; init; }

    // P4.5.3b: decision-bar cadence for BE/trail updates (minutes). The real venue evaluates
    // BE/trailing once per DECISION bar; the replayer must bucket fine-bar path points into
    // decision-bar groups and apply BE/trail only once per group. Default H1 (60 min).
    public int DecisionTfMinutes { get; init; } = 60;
}

public enum ExitKind
{
    SL,
    TP,
    Breakeven,
    TrailingStop,
    PartialTP,
    EndOfData,
}

public sealed record ExitOutcome
{
    public required ExitKind Kind { get; init; }
    public required int BarsHeld { get; init; }
    public required double RPips { get; init; }
    public required double RMultiple { get; init; }
    public required double MaePips { get; init; }
    public required double MfePips { get; init; }
}

public sealed record ExitGridCell
{
    public required ExitRule Rule { get; init; }
    public required ExitGridResult Result { get; init; }
}

public sealed record ExitGridResult
{
    public required int TradeCount { get; init; }
    public required double WinRate { get; init; }
    public required double AvgR { get; init; }
    public required double MedianR { get; init; }
    public required double AvgHoldBars { get; init; }
    public required double MaxDrawdownContributionR { get; init; }
    public required IReadOnlyList<double> TradeRValues { get; init; }
}
