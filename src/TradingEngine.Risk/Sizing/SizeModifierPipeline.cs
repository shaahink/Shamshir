using Microsoft.Extensions.Logging;

namespace TradingEngine.Risk.Sizing;

public sealed class SizeModifierPipeline
{
    private readonly IReadOnlyList<ISizeModifier> _modifiers;
    private readonly SizeModifierOptions _options;
    private readonly ILogger<SizeModifierPipeline> _logger;

    public SizeModifierPipeline(
        IEnumerable<ISizeModifier> modifiers,
        SizeModifierOptions options,
        ILogger<SizeModifierPipeline> logger)
    {
        _modifiers = modifiers.ToList();
        _options = options;
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

        var clamped = Math.Max(_options.MinCombinedScale, Math.Min(_options.MaxCombinedScale, product));
        return clamped;
    }
}
