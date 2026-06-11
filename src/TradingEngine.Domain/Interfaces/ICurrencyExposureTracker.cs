namespace TradingEngine.Domain;

public interface ICurrencyExposureTracker
{
    void Open(Guid positionId, string baseCurrency, string quoteCurrency,
              TradeDirection direction, decimal riskAmount);
    void Close(Guid positionId);
    CurrencyExposureSnapshot GetSnapshot();
    bool WouldExceedLimit(string baseCurrency, string quoteCurrency,
                          TradeDirection direction, decimal newRisk,
                          double maxPercent, decimal equity);
}
