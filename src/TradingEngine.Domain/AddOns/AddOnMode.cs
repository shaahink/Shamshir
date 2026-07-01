namespace TradingEngine.Domain;

/// <summary>
/// iter-38 (D2). How an add-on's numeric values are sourced when it is enabled:
/// <list type="bullet">
/// <item><see cref="Auto"/> — the <c>AddOnAutoTuner</c> computes them at entry from timeframe × symbol ×
/// recent volatility. The stored numbers are ignored (but kept as the editable starting point in the UI).</item>
/// <item><see cref="Custom"/> — the stored/edited numbers are used verbatim.</item>
/// </list>
/// </summary>
public enum AddOnMode
{
    Auto,
    Custom,
}
