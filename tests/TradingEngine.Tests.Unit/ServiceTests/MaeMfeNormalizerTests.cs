namespace TradingEngine.Tests.Unit.ServiceTests;

public sealed class MaeMfeNormalizerTests
{
    private static SymbolInfo EurUsd => new(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
        0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);

    private static SymbolInfo XauUsd => new(Symbol.Parse("XAUUSD"), SymbolCategory.Metal, "XAU", "USD",
        0.01m, 0.01m, 100, 0.01m, 100m, 0.01m, 0.01m, 0.2m);

    private static SymbolInfo BtcUsd => new(Symbol.Parse("BTCUSD"), SymbolCategory.Crypto, "BTC", "USD",
        1.0m, 0.01m, 1, 0.01m, 100m, 0.01m, 0.01m, 5m);

    private static SymbolInfo UsdJpy => new(Symbol.Parse("USDJPY"), SymbolCategory.Forex, "USD", "JPY",
        0.001m, 0.001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.01m);

    // P4.1 (F12): table-driven test per asset class — pips stay venue-convention,
    // R-normalized values are cross-asset-class comparable.

    [Fact]
    public void Normalize_EurUsd_Long_Winner()
    {
        var (maeR, mfeR) = MaeMfeNormalizer.Normalize(
            maePips: 12.0, mfePips: 45.0,
            entryPrice: 1.10000m, stopLoss: 1.09800m,
            EurUsd);

        // EURUSD: stop distance = (1.10000 - 1.09800) / 0.0001 = 20 pips
        // MaeR = 12 / 20 = 0.6, MfeR = 45 / 20 = 2.25
        maeR.Should().BeApproximately(0.6, 0.001);
        mfeR.Should().BeApproximately(2.25, 0.001);
    }

    [Fact]
    public void Normalize_XauUsd_ShowsCrossAssetComparability()
    {
        var (maeR, mfeR) = MaeMfeNormalizer.Normalize(
            maePips: 1668.0, mfePips: 4000.0,
            entryPrice: 2000.00m, stopLoss: 1960.00m,
            XauUsd);

        // XAUUSD: stop distance = (2000.00 - 1960.00) / 0.01 = 4000 pips
        // MaeR = 1668 / 4000 = 0.417, MfeR = 4000 / 4000 = 1.0
        maeR.Should().BeApproximately(0.417, 0.01);
        mfeR.Should().BeApproximately(1.0, 0.01);
    }

    [Fact]
    public void Normalize_BtcUsd_LargePipSize()
    {
        var (maeR, mfeR) = MaeMfeNormalizer.Normalize(
            maePips: 1512.0, mfePips: 3000.0,
            entryPrice: 70000.00m, stopLoss: 69000.00m,
            BtcUsd);

        // BTCUSD: stop distance = (70000 - 69000) / 1.0 = 1000 pips
        // MaeR = 1512 / 1000 = 1.512, MfeR = 3000 / 1000 = 3.0
        maeR.Should().BeApproximately(1.512, 0.001);
        mfeR.Should().BeApproximately(3.0, 0.001);
    }

    [Fact]
    public void Normalize_UsdJpy_SmallPipSize()
    {
        var (maeR, mfeR) = MaeMfeNormalizer.Normalize(
            maePips: 8.0, mfePips: 30.0,
            entryPrice: 150.000m, stopLoss: 150.800m,
            UsdJpy);

        // USDJPY: stop distance = (150.800 - 150.000) / 0.001 = 800 pips (short with stop above entry)
        // MaeR = 8 / 800 = 0.01, MfeR = 30 / 800 = 0.0375
        maeR.Should().BeApproximately(0.01, 0.001);
        mfeR.Should().BeApproximately(0.0375, 0.001);
    }

    [Fact]
    public void Normalize_ZeroStopDistance_ReturnsNull()
    {
        var (maeR, mfeR) = MaeMfeNormalizer.Normalize(
            maePips: 10.0, mfePips: 20.0,
            entryPrice: 1.10000m, stopLoss: 1.10000m,
            EurUsd);

        maeR.Should().BeNull();
        mfeR.Should().BeNull();
    }

    [Fact]
    public void Normalize_ZeroExcursions_ReturnsZero()
    {
        var (maeR, mfeR) = MaeMfeNormalizer.Normalize(
            maePips: 0.0, mfePips: 0.0,
            entryPrice: 1.10000m, stopLoss: 1.09800m,
            EurUsd);

        maeR.Should().Be(0.0);
        mfeR.Should().Be(0.0);
    }
}
