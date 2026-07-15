using TradingEngine.Application;

namespace TradingEngine.Tests.Unit.Services;

/// <summary>
/// F34 (iter-alpha-loop). Cross rates are pivoted on USD so that the account denomination is a configured
/// value rather than a code change: every currency carries ONE USD leg and any pair chains through it.
///
/// What these guard against, concretely: the previous store held two hardcoded literals (GBPUSD 1.2650,
/// USDJPY 149.50) refreshed only when a run happened to stream those very symbols. EURGBP/EURJPY/GBPJPY
/// were therefore priced off stale constants on every run that did not trade GBPUSD/USDJPY, and a
/// GBP-denominated account had no CHF→GBP / CAD→GBP / JPY→GBP leg at all.
/// </summary>
public sealed class CrossRateStoreTests
{
    [Fact]
    public void Same_currency_is_identity()
    {
        var store = new CrossRateStore();
        Assert.Equal(1m, store.Convert("USD", "USD"));
        Assert.Equal(1m, store.Convert("GBP", "GBP"));   // identity needs no rate
    }

    [Fact]
    public void Xxxusd_bar_sets_the_base_currency_leg()
    {
        var store = new CrossRateStore();
        store.ObserveBar("GBP", "USD", 1.27m);           // GBPUSD

        Assert.Equal(1.27m, store.Convert("GBP", "USD"));
        Assert.Equal(1m / 1.27m, store.Convert("USD", "GBP"));
    }

    // USD→JPY round-trips through the stored 1/150 leg, so it lands within decimal's last places rather
    // than exactly on 150. That residual is ~1e-25 relative — irrelevant to money, but asserted honestly.
    [Fact]
    public void Usdxxx_bar_sets_the_quote_currency_leg_inverted()
    {
        var store = new CrossRateStore();
        store.ObserveBar("USD", "JPY", 150m);            // USDJPY

        Assert.Equal(150m, store.Convert("USD", "JPY"), precision: 10);
        Assert.Equal(1m / 150m, store.Convert("JPY", "USD"), precision: 10);
    }

    // The reason for the USD pivot: a GBP account must price a CHF-quoted symbol, and nobody quotes CHFGBP.
    [Fact]
    public void Arbitrary_pair_chains_through_usd()
    {
        var store = new CrossRateStore();
        store.ObserveBar("USD", "CHF", 0.80m);           // USDCHF → 1 CHF = 1.25 USD
        store.ObserveBar("GBP", "USD", 1.25m);           // GBPUSD → 1 GBP = 1.25 USD

        // 1 CHF = 1.25 USD, 1 GBP = 1.25 USD ⇒ CHF/GBP = 1.0
        Assert.Equal(1m, store.Convert("CHF", "GBP"));
    }

    [Fact]
    public void A_cross_symbol_teaches_no_usd_leg_and_is_ignored()
    {
        var store = new CrossRateStore();
        Assert.False(store.ObserveBar("EUR", "GBP", 0.85m));   // EURGBP carries no USD leg
        Assert.False(store.HasRate("EUR"));
    }

    // F9 (iter-26) kept: a missing leg is a silent financial error (wrong pip value → wrong lot size),
    // so it throws rather than defaulting to 1.
    [Fact]
    public void Missing_leg_throws_rather_than_defaulting_to_one()
    {
        var store = new CrossRateStore();
        var ex = Assert.Throws<InvalidOperationException>(() => store.Convert("JPY", "USD"));
        Assert.Contains("JPY", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_positive_rate_is_rejected()
    {
        var store = new CrossRateStore();
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SetUsdPerUnit("GBP", 0m));
    }
}

public sealed class CrossRateSeriesLoaderTests
{
    private static SymbolInfo Sym(string name, string b, string q) =>
        new(Symbol.Parse(name), SymbolCategory.Forex, b, q,
            0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.033m, 0.00010m);

    private static readonly IReadOnlyList<SymbolInfo> Book =
    [
        Sym("EURUSD", "EUR", "USD"),
        Sym("GBPUSD", "GBP", "USD"),
        Sym("USDJPY", "USD", "JPY"),
        Sym("USDCHF", "USD", "CHF"),
        Sym("EURJPY", "EUR", "JPY"),
    ];

    [Fact]
    public void Usd_account_on_a_usd_pair_needs_no_legs()
    {
        var required = CrossRateSeriesLoader.RequiredCurrencies("USD", [Sym("EURUSD", "EUR", "USD")]);
        Assert.Equal(["EUR"], required);   // EUR only; USD is the pivot
    }

    // The live bug this closes: EURJPY on a USD account needs the JPY leg, which the run never streams.
    [Fact]
    public void Cross_symbol_requires_the_leg_it_never_streams()
    {
        var required = CrossRateSeriesLoader.RequiredCurrencies("USD", [Sym("EURJPY", "EUR", "JPY")]);
        Assert.Contains("JPY", required);
        Assert.Contains("EUR", required);
    }

    // The future-proofing claim, asserted: a GBP account adds exactly one leg (GBP) to a USD-pair run.
    [Fact]
    public void Gbp_account_adds_the_gbp_leg()
    {
        var required = CrossRateSeriesLoader.RequiredCurrencies("GBP", [Sym("EURUSD", "EUR", "USD")]);
        Assert.Contains("GBP", required);
        Assert.Contains("EUR", required);
        Assert.DoesNotContain("USD", required);
    }

    [Theory]
    [InlineData("GBP", "GBPUSD")]
    [InlineData("JPY", "USDJPY")]
    [InlineData("CHF", "USDCHF")]
    public void Resolves_each_currency_to_its_usd_leg_symbol(string currency, string expected)
    {
        var leg = CrossRateSeriesLoader.ResolveUsdLegSymbol(Book, currency);
        Assert.Equal(expected, leg?.Symbol.Value);
    }

    [Fact]
    public void A_currency_with_no_usd_leg_in_the_book_is_unresolvable()
    {
        Assert.Null(CrossRateSeriesLoader.ResolveUsdLegSymbol(Book, "CAD"));
    }
}
