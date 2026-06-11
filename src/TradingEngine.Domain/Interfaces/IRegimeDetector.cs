namespace TradingEngine.Domain;

public interface IRegimeDetector
{
    MarketRegime Detect(Symbol symbol, IReadOnlyList<Bar> bars,
        IReadOnlyDictionary<string, double> indicators);
}
