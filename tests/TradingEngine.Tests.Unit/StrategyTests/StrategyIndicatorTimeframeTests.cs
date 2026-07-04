using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Strategies.BollingerSqueeze;
using TradingEngine.Strategies.EmaAlignment;
using TradingEngine.Strategies.MacdMomentum;
using TradingEngine.Strategies.MeanReversion;
using TradingEngine.Strategies.MtfTrend;
using TradingEngine.Strategies.RsiDivergence;
using TradingEngine.Strategies.SessionBreakout;
using TradingEngine.Strategies.SuperTrend;
using TradingEngine.Strategies.TrendBreakout;

namespace TradingEngine.Tests.Unit.StrategyTests;

/// <summary>
/// P1.5.1 gate: every strategy's <see cref="IStrategy.RequiredIndicators"/> must request its OWN
/// EntryTimeframe, not an implicit/hardcoded H1. <see cref="IndicatorRequest.Timeframe"/> defaults to
/// H1 (IndicatorRequest.cs:4); a strategy that omits the parameter silently asks for H1 bars that were
/// never loaded on a non-H1 run — IndicatorSnapshotService.RecomputeIndicatorsAsync (line ~52-61) misses
/// the lookup and `continue`s, so the indicator is never computed and the strategy returns null forever.
/// This is the reintroduced "0 trades on M15" bug P1 was built to fix, one layer down.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class StrategyIndicatorTimeframeTests
{
    private static readonly ISymbolInfoRegistry Registry = Substitute.For<ISymbolInfoRegistry>();
    private const Timeframe Entry = Timeframe.M15;
    private const Timeframe Higher = Timeframe.D1; // deliberately != Entry, so mtf-trend's EMA request is distinguishable

    [Fact]
    public void TrendBreakout_RequestsIndicators_OnEntryTimeframe()
    {
        var strategy = new TrendBreakoutStrategy(new TrendBreakoutConfig { EntryTimeframe = Entry },
            Registry, NullLogger<TrendBreakoutStrategy>.Instance);
        strategy.RequiredIndicators.Should().OnlyContain(r => r.Timeframe == Entry);
    }

    [Fact]
    public void SuperTrend_RequestsIndicators_OnEntryTimeframe()
    {
        var strategy = new SuperTrendStrategy(new SuperTrendConfig { EntryTimeframe = Entry },
            Registry, NullLogger<SuperTrendStrategy>.Instance);
        strategy.RequiredIndicators.Should().OnlyContain(r => r.Timeframe == Entry);
    }

    [Fact]
    public void EmaAlignment_RequestsIndicators_OnEntryTimeframe()
    {
        var strategy = new EmaAlignmentStrategy(
            new EmaAlignmentConfig("ema-alignment", "EMA Alignment", true, "standard", new()) { EntryTimeframe = Entry },
            Registry, NullLogger<EmaAlignmentStrategy>.Instance);
        strategy.RequiredIndicators.Should().OnlyContain(r => r.Timeframe == Entry);
    }

    [Fact]
    public void MacdMomentum_RequestsIndicators_OnEntryTimeframe()
    {
        var strategy = new MacdMomentumStrategy(new MacdMomentumConfig { EntryTimeframe = Entry },
            Registry, NullLogger<MacdMomentumStrategy>.Instance);
        strategy.RequiredIndicators.Should().OnlyContain(r => r.Timeframe == Entry);
    }

    [Fact]
    public void MeanReversion_RequestsIndicators_OnEntryTimeframe()
    {
        var strategy = new MeanReversionStrategy(
            new MeanReversionConfig("mean-reversion", "Mean Reversion", true, "standard", new()) { EntryTimeframe = Entry },
            Registry, NullLogger<MeanReversionStrategy>.Instance);
        strategy.RequiredIndicators.Should().OnlyContain(r => r.Timeframe == Entry);
    }

    [Fact]
    public void SessionBreakout_RequestsIndicators_OnEntryTimeframe()
    {
        var strategy = new SessionBreakoutStrategy(
            new SessionBreakoutConfig("session-breakout", "Session Breakout", true, "standard", new()) { EntryTimeframe = Entry },
            Registry, NullLogger<SessionBreakoutStrategy>.Instance);
        strategy.RequiredIndicators.Should().OnlyContain(r => r.Timeframe == Entry);
    }

    [Fact]
    public void BollingerSqueeze_RequestsIndicators_OnEntryTimeframe()
    {
        var strategy = new BollingerSqueezeStrategy(new BollingerSqueezeConfig { EntryTimeframe = Entry },
            Registry, NullLogger<BollingerSqueezeStrategy>.Instance);
        strategy.RequiredIndicators.Should().OnlyContain(r => r.Timeframe == Entry);
    }

    [Fact]
    public void RsiDivergence_RequestsIndicators_OnEntryTimeframe()
    {
        var strategy = new RsiDivergenceStrategy(new RsiDivergenceConfig { EntryTimeframe = Entry },
            Registry, NullLogger<RsiDivergenceStrategy>.Instance);
        strategy.RequiredIndicators.Should().OnlyContain(r => r.Timeframe == Entry);
    }

    [Fact]
    public void MtfTrend_RequestsRsiAndAtr_OnEntryTimeframe_AndEma_OnHigherTimeframe()
    {
        var strategy = new MtfTrendStrategy(
            new MtfTrendConfig { EntryTimeframe = Entry, HigherTimeframe = Higher },
            Registry, NullLogger<MtfTrendStrategy>.Instance);

        var requests = strategy.RequiredIndicators;
        requests.Where(r => r.Type is IndicatorType.Rsi or IndicatorType.Atr)
            .Should().OnlyContain(r => r.Timeframe == Entry, "RSI/ATR read the decision timeframe");
        requests.Where(r => r.Type == IndicatorType.Ema)
            .Should().OnlyContain(r => r.Timeframe == Higher, "the EMA trend filter reads the higher timeframe");
    }
}
