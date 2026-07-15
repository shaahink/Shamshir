namespace TradingEngine.Domain;

public interface IEquitySink
{
    void Observe(AccountSnapshot snapshot);
}
