namespace TradingEngine.Services.Helpers;

/// <summary>A confirmed swing pivot: <see cref="Index"/> is the bar's position in the list passed to
/// <see cref="PivotFinder"/>, <see cref="Price"/> is the pivot bar's Low (swing low) or High (swing high).</summary>
public readonly record struct Pivot(int Index, decimal Price);

/// <summary>
/// Pure fractal pivot detection (P2.2). A swing low at index i requires bar[i].Low to be strictly less
/// than the Low of <paramref name="strength"/> bars on EACH side; a swing high is the mirror on High. A
/// pivot can only be confirmed once <paramref name="strength"/> bars have closed after it, so the most
/// recent <paramref name="strength"/> bars in the list can never themselves be returned as pivots — this
/// is what makes the detection non-repainting (a pivot, once returned, never changes on later calls with
/// more bars appended).
/// </summary>
public static class PivotFinder
{
    public static IReadOnlyList<Pivot> FindSwingLows(IReadOnlyList<Bar> bars, int strength)
    {
        if (strength < 1) throw new ArgumentOutOfRangeException(nameof(strength), "Pivot strength must be >= 1.");
        var pivots = new List<Pivot>();
        for (var i = strength; i <= bars.Count - 1 - strength; i++)
        {
            var isPivot = true;
            for (var k = 1; k <= strength && isPivot; k++)
            {
                if (bars[i].Low >= bars[i - k].Low || bars[i].Low >= bars[i + k].Low)
                    isPivot = false;
            }
            if (isPivot) pivots.Add(new Pivot(i, bars[i].Low));
        }
        return pivots;
    }

    public static IReadOnlyList<Pivot> FindSwingHighs(IReadOnlyList<Bar> bars, int strength)
    {
        if (strength < 1) throw new ArgumentOutOfRangeException(nameof(strength), "Pivot strength must be >= 1.");
        var pivots = new List<Pivot>();
        for (var i = strength; i <= bars.Count - 1 - strength; i++)
        {
            var isPivot = true;
            for (var k = 1; k <= strength && isPivot; k++)
            {
                if (bars[i].High <= bars[i - k].High || bars[i].High <= bars[i + k].High)
                    isPivot = false;
            }
            if (isPivot) pivots.Add(new Pivot(i, bars[i].High));
        }
        return pivots;
    }
}
