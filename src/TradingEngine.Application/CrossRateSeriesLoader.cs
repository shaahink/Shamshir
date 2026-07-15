namespace TradingEngine.Application;

/// <summary>
/// Works out which USD legs a run must be able to price, and loads them from stored market data.
///
/// This is the piece that makes the account denomination a configuration value (F34). The run declares
/// its account currency and its traded symbols; every currency involved is resolved to the ONE symbol
/// carrying its USD leg (GBP→GBPUSD, JPY→USDJPY, CHF→USDCHF, …) and that symbol's bars become the rate
/// series. A GBP account therefore needs no new conversion code — only GBPUSD data, which we already hold.
/// </summary>
public static class CrossRateSeriesLoader
{
    /// <summary>
    /// Currencies that must be priceable into <paramref name="accountCurrency"/>: the account itself plus
    /// every traded symbol's base and quote. USD is the pivot and needs no leg of its own.
    /// </summary>
    public static IReadOnlyList<string> RequiredCurrencies(
        string accountCurrency, IEnumerable<SymbolInfo> tradedSymbols)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { accountCurrency };
        foreach (var s in tradedSymbols)
        {
            set.Add(s.BaseCurrency);
            set.Add(s.QuoteCurrency);
        }
        set.Remove("USD");
        return [.. set];
    }

    /// <summary>The symbol carrying <paramref name="currency"/>'s USD leg, or null if the book has none.</summary>
    public static SymbolInfo? ResolveUsdLegSymbol(IEnumerable<SymbolInfo> book, string currency) =>
        book.FirstOrDefault(s =>
            (s.BaseCurrency.Equals(currency, StringComparison.OrdinalIgnoreCase) &&
             s.QuoteCurrency.Equals("USD", StringComparison.OrdinalIgnoreCase)) ||
            (s.QuoteCurrency.Equals(currency, StringComparison.OrdinalIgnoreCase) &&
             s.BaseCurrency.Equals("USD", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Load a USD-leg series per required currency over [fromUtc, toUtc].
    /// Throws when a required leg has no symbol in the book or no bars in the window — a run that cannot
    /// price its own account currency must not start.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<CrossRatePoint>>> LoadAsync(
        string accountCurrency,
        IReadOnlyList<SymbolInfo> tradedSymbols,
        IReadOnlyList<SymbolInfo> book,
        IMarketDataStore marketData,
        Timeframe rateTimeframe,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, IReadOnlyList<CrossRatePoint>>(StringComparer.OrdinalIgnoreCase);

        foreach (var currency in RequiredCurrencies(accountCurrency, tradedSymbols))
        {
            var leg = ResolveUsdLegSymbol(book, currency)
                ?? throw new InvalidOperationException(
                    $"Currency '{currency}' has no USD-leg symbol in config/symbols.json, so it cannot be " +
                    $"priced into the account currency. Add the {currency}/USD pair before running with it.");

            // A leg observed BEFORE the window's first bar keeps the series valid from bar one; without it
            // the first trades would price off the window's opening rate regardless of when they happen.
            var bars = await marketData.ReadBarsAsync(
                leg.Symbol, rateTimeframe, fromUtc.AddDays(-7), toUtc, ct);

            if (bars.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No {rateTimeframe} market data for {leg.Symbol.Value} over " +
                    $"{fromUtc:yyyy-MM-dd}..{toUtc:yyyy-MM-dd}, so the {currency}→USD cross rate cannot be " +
                    $"sourced. Sync that symbol before running (Data Manager), or the run would price every " +
                    $"pip off a guess.");
            }

            var quoteIsUsd = leg.QuoteCurrency.Equals("USD", StringComparison.OrdinalIgnoreCase);
            result[currency] = [.. bars.Select(b => new CrossRatePoint(
                b.OpenTimeUtc,
                quoteIsUsd ? b.Close : 1m / b.Close))];
        }

        return result;
    }
}
