using Microsoft.Extensions.Logging;

namespace TradingEngine.Risk.Sizing;

public sealed class SizeModifierPipeline
{
    private readonly IReadOnlyList<ISizeModifier> _modifiers;
    private readonly ILogger<SizeModifierPipeline> _logger;

    public SizeModifierPipeline(
        IEnumerable<ISizeModifier> modifiers,
        ILogger<SizeModifierPipeline> logger)
    {
        _modifiers = modifiers.ToList();
        _logger = logger;
    }

    public double ComputeCombinedScale(SizeModifierContext context)
    {
        double product = 1.0;
        foreach (var modifier in _modifiers)
        {
            var scale = modifier.ComputeScale(context);
            product *= scale;
            _logger.LogDebug("SIZE_MOD|{Name}|{Scale:F3}", modifier.Name, scale);
        }

        var opts = context.Profile.SizeModifiers;
        var clamped = Math.Max(opts.MinCombinedScale, Math.Min(opts.MaxCombinedScale, product));
        return clamped;
    }
}
