namespace TradingEngine.Domain;

public interface ITradingGovernor
{
    GovernorDecision Evaluate(GovernorContext context);
    GovernorSnapshot GetSnapshot();
    void OnTradeClosed(TradeResult result);
    void OnBar(DateTime barOpenTimeUtc);
    void OnDailyReset();
    void OnWeeklyReset();
}
