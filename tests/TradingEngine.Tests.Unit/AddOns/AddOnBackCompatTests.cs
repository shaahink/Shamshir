using System.Text.Json;

namespace TradingEngine.Tests.Unit.AddOns;

/// <summary>
/// iter-38 Stream A1: adding Enabled/Mode + DynamicSlTp to the add-on options must not break legacy strategy
/// JSON. A config written before iter-38 (no add-on flags) must still deserialize with safe defaults: baseline
/// SL/TP intact, every add-on OFF, and the optional add-ons null.
/// </summary>
[Trait("Category", "AddOns")]
[Trait("Speed", "Fast")]
public sealed class AddOnBackCompatTests
{
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void Legacy_position_management_json_deserializes_with_safe_defaults()
    {
        const string legacy = """
        {
          "StopLoss": { "Method": "AtrMultiple", "AtrMultiple": 2.0, "MaxPips": 80 },
          "TakeProfit": { "Method": "RrMultiple", "RrMultiple": 2.5 },
          "Trailing": { "Method": "AtrTrailing", "AtrMultiple": 1.2 },
          "Breakeven": { "TriggerRMultiple": 1.0, "OffsetPips": 2.0 }
        }
        """;

        var opts = JsonSerializer.Deserialize<PositionManagementOptions>(legacy, Opts);

        opts.Should().NotBeNull();
        opts!.StopLoss.AtrMultiple.Should().Be(2.0);
        opts.TakeProfit.RrMultiple.Should().Be(2.5);
        opts.Trailing.Method.Should().Be("AtrTrailing");

        // New iter-38 flags default safely: add-ons opt-in (OFF) and optional ones null.
        opts.Trailing.Enabled.Should().BeFalse();
        opts.Breakeven.Enabled.Should().BeFalse();
        opts.Trailing.Mode.Should().Be(AddOnMode.Auto);
        opts.Ride.Should().BeNull();
        opts.PartialTp.Should().BeNull();
        opts.DynamicSlTp.Should().BeNull();
    }
}
