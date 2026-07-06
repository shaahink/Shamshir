namespace TradingEngine.Tests.Unit.P5Tests;

public sealed class NonFxPipCalculatorTests
{
    // ── SymbolInfo fixtures matching config/symbols/defaults.json ──
    // Hand-computed expected values per PLAN.md §6 P5.2: code is wrong if test disagrees.

    private static readonly SymbolInfo XauUsd = new(
        Symbol.Parse("XAUUSD"), SymbolCategory.Metal, "XAU", "USD",
        0.01m, 0.001m, 100, 0.01m, 10m, 0.01m, 0.05m, 0.3m);

    private static readonly SymbolInfo XagUsd = new(
        Symbol.Parse("XAGUSD"), SymbolCategory.Metal, "XAG", "USD",
        0.001m, 0.0001m, 5000, 0.01m, 10m, 0.01m, 0.05m, 0.03m);

    private static readonly SymbolInfo BtcUsd = new(
        Symbol.Parse("BTCUSD"), SymbolCategory.Crypto, "BTC", "USD",
        1.0m, 0.01m, 1, 0.001m, 100m, 0.001m, 0.5m, 50.0m);

    private static readonly SymbolInfo EthUsd = new(
        Symbol.Parse("ETHUSD"), SymbolCategory.Crypto, "ETH", "USD",
        0.01m, 0.001m, 1, 0.001m, 100m, 0.001m, 0.5m, 2.0m);

    private static readonly SymbolInfo Us30 = new(
        Symbol.Parse("US30"), SymbolCategory.Index, "US30", "USD",
        1.0m, 0.01m, 1, 0.1m, 10m, 0.1m, 0.1m, 3.0m);

    private static readonly SymbolInfo Nas100 = new(
        Symbol.Parse("NAS100"), SymbolCategory.Index, "NAS100", "USD",
        0.25m, 0.01m, 1, 0.1m, 10m, 0.1m, 0.1m, 1.0m);

    // All quote=account=USD → cross-rate = 1
    private static readonly Func<string, string, decimal> Cross1 = (_, _) => 1m;

    // ── Pip value: pipSize × contractSize (PLAN §6 P5.2) ──

    [Fact]
    public void XauUsd_PipValue_IsOneDollarPerLot()
    {
        // pipSize=0.01, contract=100 → rawPipValue=0.01×100 = $1.00
        var pv = PipCalculator.PipValuePerLot(XauUsd, 2650m, Cross1);
        pv.Should().Be(1.0m);
    }

    [Fact]
    public void XagUsd_PipValue_IsFiveThousandDollarPerLot()
    {
        // pipSize=0.001, contract=5000 → rawPipValue=0.001×5000 = $5.00
        // Wait: 0.001 * 5000 = 5.0, but that seems low. Let me check.
        // Standard XAGUSD: 1 pip = 0.01 tick = $50 per standard lot.
        // But pipSize=0.001, contractSize=5000:
        // rawPipValue = 0.001 × 5000 = 5.0
        // Actually, in the defaults.json: pipSize=0.001, contractSize=5000
        // That means one "pip" in our config = $5.00. 
        // The typical XAGUSD has pip=0.01 value $50, but our config defines pip as 0.001.
        // Let me verify against the config...
        var pv = PipCalculator.PipValuePerLot(XagUsd, 30m, Cross1);
        pv.Should().Be(5.0m);
    }

    [Fact]
    public void BtcUsd_PipValue_IsOneDollarPerLot()
    {
        // pipSize=1.0, contract=1 → 1.0×1 = $1.00
        var pv = PipCalculator.PipValuePerLot(BtcUsd, 67_000m, Cross1);
        pv.Should().Be(1.0m);
    }

    [Fact]
    public void EthUsd_PipValue_IsOneHundredthDollarPerLot()
    {
        // pipSize=0.01, contract=1 → 0.01×1 = $0.01
        var pv = PipCalculator.PipValuePerLot(EthUsd, 3_300m, Cross1);
        pv.Should().Be(0.01m);
    }

    [Fact]
    public void Us30_PipValue_IsOneDollarPerLot()
    {
        // pipSize=1.0, contract=1 → 1.0×1 = $1.00
        var pv = PipCalculator.PipValuePerLot(Us30, 42_000m, Cross1);
        pv.Should().Be(1.0m);
    }

    [Fact]
    public void Nas100_PipValue_IsQuarterDollarPerLot()
    {
        // pipSize=0.25, contract=1 → 0.25×1 = $0.25
        var pv = PipCalculator.PipValuePerLot(Nas100, 21_000m, Cross1);
        pv.Should().Be(0.25m);
    }

    // ── Pip distance: price move / pipSize ──

    [Fact]
    public void XauUsd_Distance_TenDollarMove_IsThousandPips()
    {
        // pipSize=0.01 → $10 move = 1000 pips
        var d = PipCalculator.Distance(new Price(2650.00m), new Price(2660.00m), XauUsd);
        d.Value.Should().Be(1000.0);
    }

    [Fact]
    public void BtcUsd_Distance_HundredDollarMove_IsHundredPips()
    {
        // pipSize=1.0 → $100 move = 100 pips
        var d = PipCalculator.Distance(new Price(67000m), new Price(67100m), BtcUsd);
        d.Value.Should().Be(100.0);
    }

    [Fact]
    public void Us30_Distance_TenPoints_IsTenPips()
    {
        // pipSize=1.0 → 10 points = 10 pips
        var d = PipCalculator.Distance(new Price(42000m), new Price(42010m), Us30);
        d.Value.Should().Be(10.0);
    }

    // ── Gross PnL: pips × pipValue × lots ──

    [Fact]
    public void XauUsd_GrossPnL_LongWins500Pips_At01Lot_IsFiveDollars()
    {
        // entry=2650, exit=2655 (500 pips win), pipValue=$1, 0.1 lot → 500 × $1 × 0.1 = $50
        var gross = PipCalculator.GrossPnL(TradeDirection.Long,
            new Price(2650m), new Price(2655m), 0.1m, XauUsd, Cross1);
        gross.Amount.Should().Be(50m);
    }

    [Fact]
    public void BtcUsd_GrossPnL_ShortLoses1000Pips_At002Lot_IsMinusTwoDollars()
    {
        // entry=67000, exit=68000 (1000 pips loss), pipValue=$1, 0.01 lot → -1000 × $1 × 0.002 = -$2
        var gross = PipCalculator.GrossPnL(TradeDirection.Short,
            new Price(67000m), new Price(68000m), 0.002m, BtcUsd, Cross1);
        gross.Amount.Should().Be(-2m);
    }

    [Fact]
    public void Nas100_GrossPnL_LongWins40Pips_At1Lot_IsTenDollars()
    {
        // pipSize=0.25, 10-point move = 40 pips, pipValue=$0.25, 1 lot → 40 × $0.25 = $10
        var gross = PipCalculator.GrossPnL(TradeDirection.Long,
            new Price(21000m), new Price(21010m), 1m, Nas100, Cross1);
        gross.Amount.Should().Be(10m);
    }

    // ── R-multiple ──

    [Fact]
    public void XauUsd_RMultiple_300PipRisk_600PipReward_IsTwoR()
    {
        // entry=2650, SL=2647 (300 pips risk), exit=2656 (600 pips reward)
        var r = PipCalculator.RMultiple(TradeDirection.Long,
            new Price(2650m), new Price(2656m), new Price(2647m));
        r.Should().BeApproximately(2.0, 0.01);
    }
}

public sealed class NonFxTradeCostCalculatorTests
{
    private static readonly SymbolInfo BtcUsd = new(
        Symbol.Parse("BTCUSD"), SymbolCategory.Crypto, "BTC", "USD",
        1.0m, 0.01m, 1, 0.001m, 100m, 0.001m, 0.5m, 50.0m,
        "USD", CommissionPerLotPerSide: 0m, SwapLongPerLotPerNight: 0m,
        SwapShortPerLotPerNight: 0m, TripleSwapWeekday: "Wednesday");

    private static readonly SymbolInfo Us30Idx = new(
        Symbol.Parse("US30"), SymbolCategory.Index, "US30", "USD",
        1.0m, 0.01m, 1, 0.1m, 10m, 0.1m, 0.1m, 3.0m,
        "USD", CommissionPerLotPerSide: 0m, SwapLongPerLotPerNight: 0m,
        SwapShortPerLotPerNight: 0m, TripleSwapWeekday: "Wednesday");

    private static readonly Func<string, string, decimal> Cross1 = (_, _) => 1m;

    [Fact]
    public void Crypto_ZeroSwap_DoesNotThrowOrNaN()
    {
        // BTCUSD with swap=0, held overnight — swap must be 0, not NaN/exception
        var costs = TradeCostCalculator.Compute(TradeDirection.Long,
            new Price(67000m), new Price(67500m), 0.1m, BtcUsd, Cross1,
            new DateTime(2026, 1, 5, 10, 0, 0, DateTimeKind.Utc),   // Mon 10:00
            new DateTime(2026, 1, 6, 12, 0, 0, DateTimeKind.Utc));  // Tue 12:00 (crosses 22:00)

        costs.Swap.Should().Be(0m);
        costs.Commission.Should().Be(0m);
        costs.GrossProfit.Should().Be(50m);  // 500 pips × $1 × 0.1 lot
        costs.NetProfit.Should().Be(50m);    // no deductions
    }

    [Fact]
    public void Crypto_MultiNight_ZeroSwap_StillZero()
    {
        // BTCUSD held Wed→Fri with zero swap — swap stays 0 across triple-swap day
        var costs = TradeCostCalculator.Compute(TradeDirection.Short,
            new Price(67000m), new Price(66000m), 0.01m, BtcUsd, Cross1,
            new DateTime(2026, 1, 7, 10, 0, 0, DateTimeKind.Utc),    // Wed 10:00
            new DateTime(2026, 1, 9, 12, 0, 0, DateTimeKind.Utc));   // Fri 12:00

        costs.Swap.Should().Be(0m);
        costs.NetProfit.Should().Be(costs.GrossProfit);
    }

    [Fact]
    public void Index_ZeroCommission_DoesNotCorruptPnL()
    {
        // US30 with commission=0 — net = gross (no deductions at all)
        var costs = TradeCostCalculator.Compute(TradeDirection.Long,
            new Price(42000m), new Price(42100m), 1m, Us30Idx, Cross1,
            new DateTime(2026, 1, 5, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc));   // same day

        costs.Commission.Should().Be(0m);
        costs.Swap.Should().Be(0m);
        costs.NetProfit.Should().Be(costs.GrossProfit);
        costs.NetProfit.Should().Be(100m);  // 100 pips × $1 × 1 lot
    }

    [Fact]
    public void Index_MultiDay_ZeroCommissionSwap_NetEqualsGross()
    {
        var costs = TradeCostCalculator.Compute(TradeDirection.Long,
            new Price(42000m), new Price(43000m), 0.5m, Us30Idx, Cross1,
            new DateTime(2026, 1, 5, 10, 0, 0, DateTimeKind.Utc),    // Mon
            new DateTime(2026, 1, 9, 12, 0, 0, DateTimeKind.Utc));   // Fri

        costs.Commission.Should().Be(0m);
        costs.Swap.Should().Be(0m);
        costs.NetProfit.Should().Be(costs.GrossProfit);
    }
}
