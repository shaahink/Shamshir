using System.Collections.Concurrent;
using TradingEngine.Domain;
using TradingEngine.Domain.Interfaces;
using TradingEngine.Infrastructure.Indicators;

namespace TradingEngine.Web.Services;

/// <summary>
/// The market context every venue run must carry: the modelled account currency (F34), the
/// cross-rate series that price non-account-currency symbols, and the venue-declared symbol
/// economics (F44). Both venue paths load these through here so no option site can be missed —
/// missing one is precisely how F34 shipped (two of three sites never received the specs).
/// </summary>
public sealed class RunMarketContextLoader(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    BacktestJournal journal,
    ILogger<RunMarketContextLoader> logger)
{
    // F34: the currency every money figure in this engine is denominated in — pip values, risk sizing,
    // FTMO limits and the whole tape. A venue account in any other currency is not comparable to a tape
    // run, so the run fails instead of silently applying an FX factor to everything. Configurable via
    // Account:Currency: re-denominating to GBP is this value plus the GBPUSD data the rate feed loads.
    private const string DefaultAccountCurrency = "EUR";

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IConfiguration _configuration = configuration;
    private readonly BacktestJournal _journal = journal;
    private readonly ILogger<RunMarketContextLoader> _logger = logger;

    /// <summary>The account denomination (F34). One configured value; every symbol, cross rate and venue
    /// check reads it, so a GBP account is a config edit rather than a code change.</summary>
    public string ResolveAccountCurrency() =>
        _configuration.GetValue<string>("Account:Currency") is { Length: > 0 } c
            ? c.ToUpperInvariant()
            : DefaultAccountCurrency;

    /// <summary>
    /// P4.4 (F44): the broker's own commission/swap/contract economics, captured from a live cTrader
    /// session and persisted. EVERY EngineHostOptions built for a run must carry them — the engine host
    /// seeds its registry from symbols.json, which is fabricated, and only the cTrader leg can learn the
    /// real numbers for itself. Miss a site and that leg silently prices off fiction, which is precisely
    /// how F34 (currency) shipped: two of three option sites never received it.
    /// Never throws — an empty list just means "no venue session captured yet" and the registry warns.
    /// </summary>
    public async Task<IReadOnlyList<VenueSymbolSpec>> LoadVenueSymbolSpecsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IVenueSymbolSpecStore>().LoadAllAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load venue symbol specs — pricing falls back to symbols.json");
            return [];
        }
    }

    /// <summary>
    /// Load the USD-leg rate series this run needs (F34). Returns null when nothing needs converting — a
    /// USD account trading only USD-legged symbols — so the common path stays free.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<CrossRatePoint>>?> LoadCrossRateSeriesAsync(
        string accountCurrency,
        IReadOnlyList<Symbol> tradedSymbols,
        string solutionRoot,
        IMarketDataStore? marketData,
        DateTime fromUtc,
        DateTime toUtc,
        string runId,
        ConcurrentQueue<string> logLines,
        CancellationToken ct)
    {
        var catalog = new SymbolCatalog(solutionRoot, accountCurrency);
        var book = catalog.GetAll();
        var traded = tradedSymbols.Select(s => catalog.Resolve(s.Value)).ToList();

        var required = CrossRateSeriesLoader.RequiredCurrencies(accountCurrency, traded);
        if (required.Count == 0)
        {
            return null;
        }

        if (marketData is null)
        {
            throw new InvalidOperationException(
                $"Run needs cross rates for {string.Join(", ", required)} but no market-data store is " +
                "available to source them. A wrong cross rate is a wrong lot size, so the run stops here.");
        }

        var series = await CrossRateSeriesLoader.LoadAsync(
            accountCurrency, traded, book, marketData, Timeframe.H1, fromUtc, toUtc, ct);

        _journal.Write(runId, "LOG",
            $"[{DateTime.UtcNow:HH:mm:ss}] Cross rates ({accountCurrency} account): " +
            string.Join(", ", series.Select(kv => $"{kv.Key} {kv.Value.Count} obs")), logLines);

        return series;
    }
}
