namespace TradingEngine.Risk.Sizing;

public sealed class TimeOfDaySizeModifier : ISizeModifier
{
    public string Name => "TimeOfDay";

    public double ComputeScale(SizeModifierContext context)
    {
        var opts = context.Profile.SizeModifiers.TimeOfDay;
        if (!opts.Enabled) return 1.0;

        var now = context.UtcTimeOfDay;
        foreach (var window in opts.Windows)
        {
            if (window.StartUtc <= window.EndUtc)
            {
                if (now >= window.StartUtc && now <= window.EndUtc)
                    return window.Scale;
            }
            else
            {
                // Window crosses midnight
                if (now >= window.StartUtc || now <= window.EndUtc)
                    return window.Scale;
            }
        }

        return 1.0;
    }
}
