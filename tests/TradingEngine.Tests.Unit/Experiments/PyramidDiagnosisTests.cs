using TradingEngine.Domain.Experiments;

namespace TradingEngine.Tests.Unit.Experiments;

public sealed class PyramidDiagnosisTests
{
    // Long trade wins +3R, add at +1R → combined = 2*3R - 1R = +5R
    [Fact]
    public void Evaluate_LongWin_AddImproves()
    {
        var path = new PyramidPathPoint[]
        {
            new(0, 0.5, -0.2),
            new(60, 1.2, 0.3),    // +1R reached → add triggers
            new(120, 2.0, 0.8),
            new(180, 2.5, 1.5),
            new(240, 3.2, 2.0),   // +3R TP
        };

        var trial = new PyramidTrial { AddAtR = 1.0, RiskPips = 1, TpMultiple = 3.0, Direction = TradeDirection.Long };

        var result = PyramidDiagnosis.Evaluate(trial, path);

        Assert.True(result.Triggered);
        Assert.Equal(1, result.AddAtBar);
        Assert.Equal(3.0, result.BaseRMultiple, 4);
        Assert.Equal(5.0, result.PyramidRMultiple, 4); // 2*3 - 1
        Assert.Equal(2.0, result.Improvement, 4);
    }

    // Long trade reverses after add → combined SL at entry (0 pips)
    // Base hits SL at -1R. Pyramid: add at +1R, then combined SL at 0 → add loses 1R, original at 0R → net = -1R.
    [Fact]
    public void Evaluate_LongAddThenReversal_SameLoss()
    {
        var path = new PyramidPathPoint[]
        {
            new(0, 0.8, -0.1),
            new(60, 1.2, 0.5),    // add triggers
            new(120, 1.5, 0.8),
            new(180, 0.3, -0.5),
            new(240, 0.1, -1.2),  // base: -1.2 <= -1.0 → SL hit. pyramid: -1.2 <= 0 → combined SL hit.
        };

        var trial = new PyramidTrial { AddAtR = 1.0, RiskPips = 1, TpMultiple = 3.0, Direction = TradeDirection.Long };

        var result = PyramidDiagnosis.Evaluate(trial, path);

        Assert.True(result.Triggered);
        Assert.Equal(-1.0, result.BaseRMultiple, 4);
        // Combined SL at 0 → bar 240 Lo=-1.2 → hit → original=0, add=-1.0 → net=-1.0
        Assert.Equal(-1.0, result.PyramidRMultiple, 4);
        Assert.Equal(0.0, result.Improvement, 4);
    }

    // Long: add at +0.5R, continues to TP at +3R
    [Fact]
    public void Evaluate_LongAddAtHalfR_StrongImprovement()
    {
        var path = new PyramidPathPoint[]
        {
            new(0, 0.6, -0.1),
            new(60, 1.5, 0.3),
            new(120, 2.0, 1.0),
            new(180, 3.2, 1.8),   // TP hit
        };

        var trial = new PyramidTrial { AddAtR = 0.5, RiskPips = 1, TpMultiple = 3.0, Direction = TradeDirection.Long };

        var result = PyramidDiagnosis.Evaluate(trial, path);

        Assert.True(result.Triggered);
        Assert.Equal(0, result.AddAtBar);
        Assert.Equal(3.0, result.BaseRMultiple, 4);
        Assert.Equal(5.5, result.PyramidRMultiple, 4); // 2*3 - 0.5
        Assert.Equal(2.5, result.Improvement, 4);
    }

    // Long: never reaches add threshold → no trigger
    [Fact]
    public void Evaluate_NeverTriggers_AddNotReached()
    {
        var path = new PyramidPathPoint[]
        {
            new(0, 0.3, -0.2),
            new(60, 0.5, -0.1),
            new(120, 0.4, -1.2),  // SL hit
        };

        var trial = new PyramidTrial { AddAtR = 1.0, RiskPips = 1, TpMultiple = 3.0, Direction = TradeDirection.Long };

        var result = PyramidDiagnosis.Evaluate(trial, path);

        Assert.False(result.Triggered);
        Assert.Equal(-1, result.AddAtBar);
        Assert.Equal(result.BaseRMultiple, result.PyramidRMultiple, 6);
        Assert.Equal(0, result.Improvement);
    }

    // Short trade: add at -1R, continues to TP at -3R
    [Fact]
    public void Evaluate_ShortWin_AddImproves()
    {
        // All bars stay BELOW entry (Hi < 0) so combined SL at 0 never hits.
        var path = new PyramidPathPoint[]
        {
            new(0, -0.1, -0.5),
            new(60, -0.5, -1.2),   // add triggers (Lo <= -1)
            new(120, -1.0, -2.0),
            new(180, -2.5, -3.2),  // TP hit (Lo <= -3)
        };

        var trial = new PyramidTrial { AddAtR = 1.0, RiskPips = 1, TpMultiple = 3.0, Direction = TradeDirection.Short };

        var result = PyramidDiagnosis.Evaluate(trial, path);

        Assert.True(result.Triggered);
        Assert.Equal(1, result.AddAtBar);
        Assert.Equal(3.0, result.BaseRMultiple, 4);
        Assert.Equal(5.0, result.PyramidRMultiple, 4); // 2*3 - 1
        Assert.Equal(2.0, result.Improvement, 4);
    }

    // Short: add at -1R, then reverses to entry → combined SL hit
    [Fact]
    public void Evaluate_ShortAddThenReversal_SameLoss()
    {
        // Price stays below entry until the reversal bar which crosses above entry.
        var path = new PyramidPathPoint[]
        {
            new(0, -0.1, -0.8),
            new(60, -0.3, -1.2),   // add triggers
            new(120, -0.5, -1.5),  // continues down
            new(180, -0.2, -0.3),  // reversing
            new(240, 0.1, -0.2),   // Hi >= 0 → combined SL hit
        };

        var trial = new PyramidTrial { AddAtR = 1.0, RiskPips = 1, TpMultiple = 3.0, Direction = TradeDirection.Short };

        var result = PyramidDiagnosis.Evaluate(trial, path);

        Assert.True(result.Triggered);
        // Base: never hits SL (Hi never >= 1.0) or TP. EOD close at HiPips=0.1 → 0.1/1*(-1) = -0.1R
        Assert.Equal(-0.1, result.BaseRMultiple, 4);
        // Pyramid: combined SL at 0 → bar 4 Hi=0.1 >= 0 → hit → net = -1R
        Assert.Equal(-1.0, result.PyramidRMultiple, 4);
        Assert.Equal(-0.9, result.Improvement, 4);
    }

    // Empty path
    [Fact]
    public void Evaluate_EmptyPath_ReturnsZero()
    {
        var trial = new PyramidTrial { AddAtR = 1.0, RiskPips = 1, Direction = TradeDirection.Long };
        var result = PyramidDiagnosis.Evaluate(trial, []);

        Assert.False(result.Triggered);
        Assert.Equal(0, result.BaseRMultiple);
        Assert.Equal(0, result.PyramidRMultiple);
    }

    // No TP: end of data exit
    [Fact]
    public void Evaluate_NoTp_EndOfData()
    {
        var path = new PyramidPathPoint[]
        {
            new(0, 0.6, -0.1),
            new(60, 1.2, 0.4),    // add triggers
            new(120, 2.5, 1.0),
            new(180, 2.0, 0.5),   // EOD close at LoPips=0.5
        };

        var trial = new PyramidTrial { AddAtR = 1.0, RiskPips = 1, TpMultiple = null, Direction = TradeDirection.Long };

        var result = PyramidDiagnosis.Evaluate(trial, path);

        Assert.True(result.Triggered);
        // Base EOD: close at LoPips=0.5 → +0.5R
        Assert.Equal(0.5, result.BaseRMultiple, 4);
        // Pyramid EOD: original close=0.5, add close=0.5-1.0=-0.5 → combined=0
        Assert.Equal(0.0, result.PyramidRMultiple, 4);
        Assert.Equal(-0.5, result.Improvement, 4);
    }

    // Summarize aggregates correctly
    [Fact]
    public void Summarize_MixedOutcomes_ComputesCorrectly()
    {
        var outcomes = new[]
        {
            new PyramidOutcome { AddAtR = 1.0, Triggered = true, AddAtBar = 1, BaseRMultiple = 2.0, PyramidRMultiple = 3.0, Improvement = 1.0 },
            new PyramidOutcome { AddAtR = 1.0, Triggered = true, AddAtBar = 1, BaseRMultiple = -1.0, PyramidRMultiple = -1.0, Improvement = 0.0 },
            new PyramidOutcome { AddAtR = 1.0, Triggered = true, AddAtBar = 1, BaseRMultiple = 2.0, PyramidRMultiple = 1.0, Improvement = -1.0 },
            new PyramidOutcome { AddAtR = 1.0, Triggered = false, AddAtBar = -1, BaseRMultiple = -1.0, PyramidRMultiple = -1.0, Improvement = 0.0 },
            new PyramidOutcome { AddAtR = 1.0, Triggered = false, AddAtBar = -1, BaseRMultiple = 3.0, PyramidRMultiple = 3.0, Improvement = 0.0 },
        };

        var summary = PyramidDiagnosis.Summarize(1.0, outcomes);

        Assert.Equal(5, summary.TotalTrades);
        Assert.Equal(3, summary.Triggered);
        Assert.Equal(3.0 / 5.0, summary.TriggerRate, 4);
        Assert.Equal(1, summary.Improved);
        Assert.Equal(1, summary.Worsened);
        Assert.Equal(1, summary.Breakeven);
        Assert.Equal(1.0, summary.AvgBaseR, 4);
        Assert.Equal(1.0, summary.AvgPyramidR, 4);
        Assert.Equal(0.0, summary.AvgImprovement, 4);
    }

    // Default add levels
    [Fact]
    public void DefaultAddLevels_HasExpectedRange()
    {
        Assert.Equal(6, PyramidDiagnosis.DefaultAddLevels.Length);
        Assert.Equal(0.5, PyramidDiagnosis.DefaultAddLevels[0]);
        Assert.Equal(3.0, PyramidDiagnosis.DefaultAddLevels[^1]);
    }

    // Exact threshold triggers on the bar that reaches it
    [Fact]
    public void Evaluate_ExactThreshold_Triggers()
    {
        var path = new PyramidPathPoint[]
        {
            new(0, 0.5, -0.1),
            new(60, 1.0, 0.3),    // HiPips = 1.0 exactly
            new(120, 3.2, 1.5),   // TP
        };

        var trial = new PyramidTrial { AddAtR = 1.0, RiskPips = 1, TpMultiple = 3.0, Direction = TradeDirection.Long };

        var result = PyramidDiagnosis.Evaluate(trial, path);

        Assert.True(result.Triggered);
        Assert.Equal(1, result.AddAtBar);
    }

    // No TP, add never triggered, EOD close
    [Fact]
    public void Evaluate_NoTp_NeverTriggers_Eod()
    {
        var path = new PyramidPathPoint[]
        {
            new(0, 0.3, -0.1),
            new(60, 0.5, 0.1),
            new(120, 0.7, 0.2),
        };

        var trial = new PyramidTrial { AddAtR = 1.0, RiskPips = 1, TpMultiple = null, Direction = TradeDirection.Long };

        var result = PyramidDiagnosis.Evaluate(trial, path);

        Assert.False(result.Triggered);
        Assert.Equal(0.2, result.BaseRMultiple, 4);
        Assert.Equal(result.BaseRMultiple, result.PyramidRMultiple, 6);
        Assert.Equal(0, result.Improvement);
    }
}
