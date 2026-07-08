using TradingEngine.Domain.Experiments;

namespace TradingEngine.Tests.Unit.Experiments;

public sealed class EntryDiagnosisTests
{
    // OLS accuracy: known linear relationship y = 3 + 2*x (no noise on other features)
    [Fact]
    public void Diagnose_KnownLinear_ReturnsExactCoefficients()
    {
        var obs = new List<EntryObservation>();
        for (var i = 0; i < 20; i++)
        {
            var x = i + 1;
            obs.Add(new EntryObservation(3.0 + 2.0 * x, x, 0.0, 0, "London", "s1", "EURUSD", "H1"));
        }

        var result = EntryDiagnosis.Diagnose(obs);

        Assert.Equal(20, result.Observations);
        Assert.True(result.RSquared > 0.99,
            $"Expected near-perfect fit R², got {result.RSquared:F6}");

        var atrFeature = result.Features
            .First(f => f.Name == "AtrPercentile");
        Assert.True(Math.Abs(atrFeature.Coefficient - 2.0) < 0.1,
            $"Expected AtrPercentile coeff ≈ 2.0, got {atrFeature.Coefficient:F6}");
    }

    // OLS with session dummies: constant R per session
    // One session is dropped as reference; coefficients measure deviation from it
    [Fact]
    public void Diagnose_WithSessions_DecomposesBySession()
    {
        var obs = new List<EntryObservation>();
        var rng = new Random(12345);
        // London trades: avg R = 1.0 (reference category)
        for (var i = 0; i < 8; i++)
            obs.Add(new EntryObservation(1.0 + (rng.NextDouble() - 0.5) * 0.4, rng.NextDouble() * 3, (rng.NextDouble() - 0.5) * 4, rng.Next(10), "London", "s1", "EURUSD", "H1"));
        // NewYork trades: avg R = -0.5
        for (var i = 0; i < 8; i++)
            obs.Add(new EntryObservation(-0.5 + (rng.NextDouble() - 0.5) * 0.4, rng.NextDouble() * 3, (rng.NextDouble() - 0.5) * 4, rng.Next(10), "NewYork", "s1", "EURUSD", "H1"));

        var result = EntryDiagnosis.Diagnose(obs);

        Assert.Equal(16, result.Observations);
        Assert.True(result.RSquared > 0.3,
            $"Session should explain variance, R²={result.RSquared:F3}");

        var sessionFeatures = result.Features
            .Where(f => f.Name.Contains("Session"))
            .ToList();
        Assert.Single(sessionFeatures);
    }

    // Too few observations
    [Fact]
    public void Diagnose_FewerThanThree_ReturnsEmpty()
    {
        var obs = new[]
        {
            new EntryObservation(1.0, 0.5, 0, 0, "London", "s1", "EURUSD", "H1"),
            new EntryObservation(-1.0, 0.5, 0, 0, "London", "s1", "EURUSD", "H1"),
        };

        var result = EntryDiagnosis.Diagnose(obs);

        Assert.Equal(2, result.Observations);
        Assert.Equal(0, result.Parameters);
        Assert.Contains("Insufficient observations", result.Summary);
    }

    // Too many parameters relative to observations
    [Fact]
    public void Diagnose_TooFewForParameters_ReturnsEmpty()
    {
        var obs = new List<EntryObservation>();
        // 4 observations with many unique sessions, but features have variance so they survive
        for (var i = 0; i < 4; i++)
            obs.Add(new EntryObservation(1.0, 0.5 + i * 0.1, i * 0.1, i, $"Session{i}", "s1", "EURUSD", "H1"));

        var result = EntryDiagnosis.Diagnose(obs);

        // 4 sessions → 3 dummies + 3 features + 1 intercept = 7 params, 4 obs → params > obs
        Assert.Equal(4, result.Observations);
        Assert.True(result.Parameters > result.Observations,
            $"Parameters ({result.Parameters}) should exceed observations ({result.Observations})");
        Assert.Contains("Insufficient observations", result.Summary);
    }

    // Constant input — with 1 session + all-constant features, all features are dropped → no features left
    [Fact]
    public void Diagnose_AllSameSession_ReturnsEmpty()
    {
        var obs = new List<EntryObservation>();
        for (var i = 0; i < 10; i++)
            obs.Add(new EntryObservation(1.0, 0.5, 0, 0, "Same", "s1", "EURUSD", "H1"));

        var result = EntryDiagnosis.Diagnose(obs);

        // All features have zero variance → dropped → only intercept remains (k=1) → no features to regress
        Assert.Contains("No features to regress", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Features);
    }

    // No signal: random data → low R²
    [Fact]
    public void Diagnose_RandomData_HasLowRSquared()
    {
        var obs = new List<EntryObservation>();
        var rng = new Random(42);
        var sessions = new[] { "London", "NewYork", "Asian" };

        for (var i = 0; i < 50; i++)
        {
            obs.Add(new EntryObservation(
                (rng.NextDouble() - 0.5) * 4,  // R between [-2, 2]
                rng.NextDouble() * 3,
                (rng.NextDouble() - 0.5) * 4,
                rng.Next(10),
                sessions[rng.Next(sessions.Length)],
                "s1", "EURUSD", "H1"));
        }

        var result = EntryDiagnosis.Diagnose(obs);

        Assert.Equal(50, result.Observations);
        Assert.True(result.RSquared < 0.3,
            $"Random data should have low R², got {result.RSquared:F3}");
        Assert.NotEmpty(result.Features);
        Assert.NotNull(result.Summary);
    }

    // ATR computation: known TR series
    [Fact]
    public void ComputeAtr_SimpleSeries_ReturnsCorrectValue()
    {
        // 5 bars: H=10, L=5, C=7 (previous close 0 for first bar)
        var high = new List<double> { 10, 11, 12, 13, 14 };
        var low = new List<double> { 5, 6, 7, 8, 9 };
        var close = new List<double> { 7, 8, 9, 10, 11 };

        // TR: [10-5=5, 11-6=5, 12-7=5, 13-8=5, 14-9=5] (no gaps, all TR=self-range)
        // Also need to check: |H-prevC|, |L-prevC|
        // bar1 TR = max(11-6=5, |11-7|=4, |6-7|=1) = 5
        // bar2 TR = max(12-7=5, |12-8|=4, |7-8|=1) = 5
        // bar3 TR = max(13-8=5, |13-9|=4, |8-9|=1) = 5
        // bar4 TR = max(14-9=5, |14-10|=4, |9-10|=1) = 5
        // ATR(4) = (5+5+5+5)/4 = 5
        var atr = EntryDiagnosis.ComputeAtr(high, low, close, 4);
        Assert.Equal(5.0, atr, 4);
    }

    // EMA computation
    [Fact]
    public void ComputeEma_SimpleSeries_ReturnsCorrectValue()
    {
        // SMA(3) seed from last 3 values of [1,2,3,4,5] → (3+4+5)/3 = 4
        var close = new List<double> { 1, 2, 3, 4, 5 };
        var ema = EntryDiagnosis.ComputeEma(close, 3);
        // multiplier = 2/(3+1) = 0.5
        // seed = (3+4+5)/3 = 4
        // iterate from index 2 (3): ema = (3-4)*0.5+4 = 3.5
        // index 3 (4): ema = (4-3.5)*0.5+3.5 = 3.75
        // index 4 (5): ema = (5-3.75)*0.5+3.75 = 4.375
        Assert.Equal(4.375, ema, 6);
    }

    // SMA computation
    [Fact]
    public void ComputeSma_SimpleSeries_ReturnsCorrectValue()
    {
        var close = new List<double> { 1, 2, 3, 4, 5 };
        var sma = EntryDiagnosis.ComputeSma(close, 3);
        // (3+4+5)/3 = 4
        Assert.Equal(4.0, sma, 6);
    }

    // Squeeze age: constant width series
    [Fact]
    public void ComputeSqueezeAge_ConstantWidth_ReturnsPositive()
    {
        var n = 40;
        var high = new List<double>();
        var low = new List<double>();
        var close = new List<double>();

        for (var i = 0; i < n; i++)
        {
            var p = 100.0 + i * 0.01;
            high.Add(p + 0.1);
            low.Add(p - 0.1);
            close.Add(p);
        }

        var age = EntryDiagnosis.ComputeSqueezeAge(high, low, close, 14, 10);
        // With near-constant bars (tiny std relative to price), BB widths are all ~0,
        // threshold ~0 → most recent bars in squeeze. Floating-point variance may give 3+
        Assert.True(age >= 3, $"Constant narrow bars should have high squeeze age, got {age}");
    }

    // Squeeze age: volatile then quiet
    [Fact]
    public void ComputeSqueezeAge_QuietingSeries_ReturnsPositiveAge()
    {
        // First bars: large swings (high std/sma). Last bars: tiny swings (low std/sma).
        var n = 50;
        var high = new List<double>();
        var low = new List<double>();
        var close = new List<double>();

        for (var i = 0; i < n; i++)
        {
            var basePrice = 100.0 + i * 0.05;
            var swing = i < 25 ? 2.0 : 0.05;
            high.Add(basePrice + swing);
            low.Add(basePrice - swing);
            close.Add(basePrice + (i % 2 == 0 ? swing * 0.3 : -swing * 0.3));
        }

        var age = EntryDiagnosis.ComputeSqueezeAge(high, low, close, 14, 10);
        // Last 10+ bars are quiet → BB width should drop → squeeze age > 0
        Assert.True(age > 0, $"Quieting series should have positive squeeze age, got {age}");
        Assert.True(age <= 10, $"Squeeze age should not exceed lookback, got {age}");
    }
}
