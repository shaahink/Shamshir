namespace TradingEngine.Tests.Unit.Phase3BTests;

[Trait("Category", "Services")]
public sealed class ExitReasonTests
{
    [Fact]
    public void LongExit_BelowSl_IsSl()
    {
        var result = ComputeExitReason(TradeDirection.Long, 1.08250m, 1.08300m);
        result.Should().Be("SL");
    }

    [Fact]
    public void LongExit_AboveSl_IsTp()
    {
        var result = ComputeExitReason(TradeDirection.Long, 1.08900m, 1.08300m);
        result.Should().Be("TP");
    }

    [Fact]
    public void ShortExit_AboveSl_IsSl()
    {
        var result = ComputeExitReason(TradeDirection.Short, 1.08750m, 1.08700m);
        result.Should().Be("SL");
    }

    [Fact]
    public void ShortExit_BelowSl_IsTp()
    {
        var result = ComputeExitReason(TradeDirection.Short, 1.08400m, 1.08700m);
        result.Should().Be("TP");
    }

    private static string ComputeExitReason(TradeDirection dir, decimal exitPrice, decimal stopLoss)
    {
        return dir == TradeDirection.Long
            ? (exitPrice <= stopLoss ? "SL" : "TP")
            : (exitPrice >= stopLoss ? "SL" : "TP");
    }
}
