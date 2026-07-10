using TradingEngine.Services.Helpers;

namespace TradingEngine.Tests.Unit.Phase45Tests;

[Trait("Category", "Unit")]
public sealed class PlateauPickerTests
{
    private static PlateauCell Cell(decimal param, decimal netProfit, double winRate, decimal dd = 0) =>
        new("SlAtrMultiple", param, netProfit, winRate, dd, null);

    [Fact]
    public void Empty_ReturnsNull() => PlateauPicker.Pick([]).Should().BeNull();

    [Fact]
    public void SingleCell_ReturnsIt() => PlateauPicker.Pick([Cell(1.5m, 500m, 60)]).Should().NotBeNull();

    [Fact]
    public void LessThanThree_RanksByProfitThenDrawdown()
    {
        PlateauPicker.Pick([Cell(1.5m, 400m, 50), Cell(2.0m, 600m, 45)])!.Value
            .ParamValue.Should().Be(2.0m);
    }

    [Fact]
    public void Plateau_NeighborhoodMedian_WinsOverIsolatedPeak()
    {
        // Tall isolated peak at 1.5 hurt by neighborhood; plateau around 3.0-3.5-4.0 wins.
        // Both 3.0 and 3.5 have median NetProfit=300 and median WinRate=50.
        // Tiebreak: smaller param (3.0) wins — the conservative plateau center.
        var cells = new[]
        {
            Cell(1.0m, -500m, 30),
            Cell(1.5m, 700m, 35),   // isolated peak — neighborhood median is low
            Cell(2.0m, -200m, 20),
            Cell(2.5m, 100m, 40),
            Cell(3.0m, 300m, 50),
            Cell(3.5m, 350m, 52),   // same neighborhood median as 3.0
            Cell(4.0m, 200m, 48),
        };
        var best = PlateauPicker.Pick(cells);
        best.Should().NotBeNull();
        best!.Value.ParamValue.Should().Be(3.0m, "ties break to smaller param (conservative)");
    }

    [Fact]
    public void TieBreak_ProfitWinRateEqual_ReturnsSmallerParam()
    {
        var cells = new[] { Cell(2.0m, 500m, 50), Cell(1.5m, 500m, 50) };
        PlateauPicker.Pick(cells)!.Value.ParamValue.Should().Be(1.5m);
    }

    [Fact]
    public void Errors_Skipped()
    {
        var cells = new[]
        {
            Cell(1.5m, 300m, 40),
            new PlateauCell("SlAtrMultiple", 2.0m, 1000m, 99, 0, "timeout"),
            Cell(2.5m, 200m, 30),
        };
        PlateauPicker.Pick(cells)!.Value.ParamValue.Should().Be(1.5m);
    }
}
