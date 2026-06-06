namespace TradingEngine.Services.Helpers;

public sealed class ExcursionTracker
{
    private decimal _worstAdverse;
    private decimal _bestFavorable;

    public Pips Mae => new((double)_worstAdverse);
    public Pips Mfe => new((double)_bestFavorable);

    public void Update(Position position, Tick tick, SymbolInfo symbol)
    {
        decimal adverse, favorable;

        if (position.Direction == TradeDirection.Long)
        {
            adverse = Math.Max(0m, position.EntryPrice.Value - tick.Bid) / symbol.PipSize;
            favorable = Math.Max(0m, tick.Bid - position.EntryPrice.Value) / symbol.PipSize;
        }
        else
        {
            adverse = Math.Max(0m, tick.Ask - position.EntryPrice.Value) / symbol.PipSize;
            favorable = Math.Max(0m, position.EntryPrice.Value - tick.Ask) / symbol.PipSize;
        }

        _worstAdverse = Math.Max(_worstAdverse, adverse);
        _bestFavorable = Math.Max(_bestFavorable, favorable);
    }

    public void Reset()
    {
        _worstAdverse = 0;
        _bestFavorable = 0;
    }
}
