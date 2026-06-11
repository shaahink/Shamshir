namespace TradingEngine.Risk;

public sealed class CurrencyExposureTracker : ICurrencyExposureTracker
{
    private readonly Dictionary<Guid, PositionExposure> _positions = new();

    public void Open(Guid positionId, string baseCurrency, string quoteCurrency,
                     TradeDirection direction, decimal riskAmount)
    {
        _positions[positionId] = new PositionExposure(baseCurrency, quoteCurrency, direction, riskAmount);
    }

    public void Close(Guid positionId)
    {
        _positions.Remove(positionId);
    }

    public CurrencyExposureSnapshot GetSnapshot()
    {
        var netRisk = new Dictionary<string, decimal>();
        decimal totalCorrelated = 0;

        foreach (var (_, exposure) in _positions)
        {
            var signedRisk = exposure.Direction == TradeDirection.Long ? exposure.Risk : -exposure.Risk;

            netRisk.TryGetValue(exposure.Base, out var baseRisk);
            netRisk[exposure.Base] = baseRisk + signedRisk;

            netRisk.TryGetValue(exposure.Quote, out var quoteRisk);
            netRisk[exposure.Quote] = quoteRisk - signedRisk;
        }

        totalCorrelated = netRisk.Values.Sum(v => Math.Abs(v));
        return new CurrencyExposureSnapshot { NetRiskByCurrency = netRisk, TotalCorrelatedRisk = totalCorrelated };
    }

    public bool WouldExceedLimit(string baseCurrency, string quoteCurrency,
                                  TradeDirection direction, decimal newRisk,
                                  double maxPercent, decimal equity)
    {
        var snapshot = GetSnapshot();
        var simulated = new Dictionary<string, decimal>(snapshot.NetRiskByCurrency);

        var signed = direction == TradeDirection.Long ? newRisk : -newRisk;
        simulated.TryGetValue(baseCurrency, out var baseR);
        simulated[baseCurrency] = baseR + signed;
        simulated.TryGetValue(quoteCurrency, out var quoteR);
        simulated[quoteCurrency] = quoteR - signed;

        var maxRiskPerCurrency = equity * (decimal)maxPercent;
        return simulated.Values.Any(v => Math.Abs(v) > maxRiskPerCurrency);
    }

    private sealed record PositionExposure(
        string Base, string Quote, TradeDirection Direction, decimal Risk);
}
