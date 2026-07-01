namespace TradingEngine.Domain;

/// <summary>
/// iter-38 add-on (Stream A6). When <see cref="Enabled"/>, an auto-tuned ATR-based stop/target REPLACES the
/// strategy's baseline <see cref="SlOptions"/>/<see cref="TpOptions"/> numbers (the baseline stays the
/// fallback when this is off — owner decision D4: a strategy ALWAYS resolves a valid SL+TP).
///
/// With <see cref="AddOnMode.Auto"/> the <c>AddOnAutoTuner</c> supplies <see cref="AtrMultipleSl"/> /
/// <see cref="RrMultipleTp"/> at entry from (timeframe, symbol, volatility); with
/// <see cref="AddOnMode.Custom"/> the stored values are used.
/// </summary>
public record DynamicSlTpOptions
{
    public bool Enabled { get; init; }
    public AddOnMode Mode { get; init; } = AddOnMode.Auto;
    public double AtrMultipleSl { get; init; } = 1.5;
    public double RrMultipleTp { get; init; } = 2.0;
}
