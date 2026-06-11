namespace TradingEngine.Domain;

public interface IStrategyBank
{
    IReadOnlyList<IStrategy> GetActive(Symbol symbol, Timeframe timeframe, MarketRegime regime);
    IReadOnlyList<IStrategy> GetAll();
    void Enable(string strategyId);
    void Disable(string strategyId);
    void NotifyResult(string strategyId, TradeResult result);
    StrategyBankSnapshot GetSnapshot();
}
