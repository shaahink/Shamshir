using TradingEngine.Services.AddOns;
using TradingEngine.Services.SLTPCalculation;

namespace TradingEngine.Tests.Unit.AddOns;

/// <summary>
/// iter-38 A6 / D4: the DynamicSlTp add-on replaces the baseline SL/TP with an ATR-based stop and an
/// RR-based target. In Auto mode the distances track the <see cref="AddOnAutoTuner"/>; in Custom mode they
/// track the stored multiples. (The seam that applies this is BarEvaluator, gated on Enabled — off ⇒ the
/// strategy's own SL/TP stand, keeping the golden path byte-identical.)
/// </summary>
[Trait("Category", "AddOns")]
[Trait("Speed", "Fast")]
public sealed class DynamicSlTpTests
{
    private static readonly SymbolInfo Eurusd = new(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
        0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);

    [Fact]
    public void Auto_dynamic_sl_tp_track_the_tuner()
    {
        var entry = new Price(1.10000m);
        const double atrPrice = 0.0010; // 10 pips
        var vol = new VolatilityContext(AtrPips: atrPrice / (double)Eurusd.PipSize, TypicalSpreadPips: 1, ReferenceAtrPips: 0);
        var tuned = AddOnAutoTuner.Tune(Timeframe.H1, vol);

        var sl = SlTpHelpers.AtrBased(entry, TradeDirection.Long, atrPrice, tuned.DynamicSlAtrMultiple, Eurusd);
        var slDist = entry.Value - sl.Value;
        slDist.Should().BeApproximately((decimal)(atrPrice * tuned.DynamicSlAtrMultiple), 0.00002m);

        var tp = SlTpHelpers.RRMultiple(entry, sl, TradeDirection.Long, tuned.DynamicTpRrMultiple, Eurusd);
        tp.Should().NotBeNull();
        var tpDist = tp!.Value.Value - entry.Value;
        tpDist.Should().BeApproximately(slDist * (decimal)tuned.DynamicTpRrMultiple, 0.00002m);
    }

    [Fact]
    public void Custom_dynamic_sl_uses_stored_multiple()
    {
        var entry = new Price(1.10000m);
        const double atrPrice = 0.0010;
        var dyn = new DynamicSlTpOptions { Enabled = true, Mode = AddOnMode.Custom, AtrMultipleSl = 2.0, RrMultipleTp = 3.0 };

        var sl = SlTpHelpers.AtrBased(entry, TradeDirection.Long, atrPrice, dyn.AtrMultipleSl, Eurusd);
        (entry.Value - sl.Value).Should().BeApproximately((decimal)(atrPrice * dyn.AtrMultipleSl), 0.00002m);
    }
}
