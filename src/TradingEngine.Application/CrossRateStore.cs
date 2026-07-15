using System.Collections.Concurrent;

namespace TradingEngine.Application;

/// <summary>
/// Cross-rate table pivoted on USD. Every currency is held as "how many USD one unit of it buys"
/// (USD itself = 1), so an arbitrary pair converts by chaining two legs. Adding a currency means
/// supplying its ONE USD leg, not a leg per pair — which is what makes the account denomination a
/// configuration value (<c>Account:Currency</c>) rather than a code change: a GBP account converts
/// CHF→GBP through CHF→USD→GBP with no new conversion branch.
///
/// Rates are fed from market data (<see cref="ObserveBar"/> / <see cref="SetUsdPerUnit"/>), never
/// invented. The previous implementation carried two hardcoded constants (GBPUSD 1.2650, USDJPY
/// 149.50) that were only refreshed when a run happened to stream those very symbols — so EURGBP,
/// EURJPY and GBPJPY were priced off stale literals on every run that did not trade GBPUSD/USDJPY.
/// </summary>
public sealed class CrossRateStore
{
    private readonly ConcurrentDictionary<string, decimal> _usdPerUnit =
        new(StringComparer.OrdinalIgnoreCase) { ["USD"] = 1m };

    /// <summary>Set a currency's USD leg: how many USD one unit of <paramref name="currency"/> buys.</summary>
    public void SetUsdPerUnit(string currency, decimal usdPerUnit)
    {
        if (string.IsNullOrWhiteSpace(currency))
        {
            throw new ArgumentException("Currency required", nameof(currency));
        }

        if (usdPerUnit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(usdPerUnit),
                $"Cross rate for {currency} must be positive (got {usdPerUnit}).");
        }

        if (string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _usdPerUnit[currency] = usdPerUnit;
    }

    /// <summary>
    /// Feed a market bar. A bar on a USD pair updates that pair's non-USD currency; anything else is
    /// ignored (a cross like EURGBP carries no USD leg, so it teaches us nothing directly).
    /// </summary>
    public bool ObserveBar(string baseCurrency, string quoteCurrency, decimal close)
    {
        if (close <= 0) return false;

        if (string.Equals(quoteCurrency, "USD", StringComparison.OrdinalIgnoreCase))
        {
            SetUsdPerUnit(baseCurrency, close);          // XXXUSD: 1 XXX = close USD
            return true;
        }

        if (string.Equals(baseCurrency, "USD", StringComparison.OrdinalIgnoreCase))
        {
            SetUsdPerUnit(quoteCurrency, 1m / close);    // USDXXX: 1 XXX = 1/close USD
            return true;
        }

        return false;
    }

    public bool HasRate(string currency) =>
        string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase) ||
        (_usdPerUnit.TryGetValue(currency, out var r) && r > 0);

    public decimal Convert(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return 1m;
        return UsdLeg(from) / UsdLeg(to);
    }

    // F9 (iter-26): never silently return 1 for an unknown leg — that is a silent financial error
    // (wrong pip value → wrong lot size → wrong PnL). Fail loud so a missing rate is caught at the
    // point of use rather than discovered in a reconciliation months later.
    private decimal UsdLeg(string currency)
    {
        if (_usdPerUnit.TryGetValue(currency, out var rate) && rate > 0) return rate;
        throw new InvalidOperationException(
            $"No USD cross rate for '{currency}'. Feed the {currency}/USD leg from market data " +
            $"(CrossRateStore.ObserveBar / SetUsdPerUnit) before pricing a symbol that needs it.");
    }
}
