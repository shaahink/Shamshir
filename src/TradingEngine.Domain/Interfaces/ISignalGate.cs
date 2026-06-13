namespace TradingEngine.Domain;

public interface ISignalGate
{
    SignalGateResult Check(string strategyId, string symbol, TradeDirection direction, DateTime barTimeUtc);
    void RegisterStrategy(IStrategyConfig config);
    void OnPositionOpened(string strategyId, string symbol, TradeDirection direction, DateTime barTimeUtc);
    void OnPositionClosed(string strategyId, string symbol, TradeDirection direction, string reason, DateTime barTimeUtc);
    void OnBar(DateTime barTimeUtc);
}

public record SignalGateResult(bool Allowed, string Reason);
