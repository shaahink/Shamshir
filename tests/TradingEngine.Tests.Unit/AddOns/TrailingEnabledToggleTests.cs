namespace TradingEngine.Tests.Unit.AddOns;

/// <summary>
/// iter-38 A1 (toggle wiring fix): the Trailing add-on is gated by its universal <c>Enabled</c> flag, just like
/// every other add-on. <see cref="PositionManager.BuildConfig"/> maps a configured trailing <c>Method</c> onto an
/// active trail ONLY when <c>Trailing.Enabled</c> is true — a Method without Enabled is OFF. This pins the toggle
/// so it can't silently regress to the old Method-driven behaviour, where the UI/pack "enable trailing" switch
/// was a no-op and the auto-tuner never ran on a strategy that trailed via Method alone.
/// </summary>
[Trait("Category", "AddOns")]
[Trait("Speed", "Fast")]
public sealed class TrailingEnabledToggleTests
{
    [Fact]
    public void Method_without_Enabled_resolves_to_no_trailing()
    {
        var opts = new PositionManagementOptions
        {
            Trailing = new TrailingOptions { Enabled = false, Method = "AtrMultiple", AtrMultiple = 2.5 },
        };

        var config = PositionManager.BuildConfig("s", opts, 0m);

        config.TrailingStop.Method.Should().Be(TrailingMethod.None,
            "a configured Method must NOT activate trailing unless the add-on is explicitly enabled");
    }

    [Fact]
    public void Enabled_with_Custom_keeps_the_method_and_stored_numbers()
    {
        var opts = new PositionManagementOptions
        {
            Trailing = new TrailingOptions { Enabled = true, Mode = AddOnMode.Custom, Method = "AtrMultiple", AtrMultiple = 2.5 },
        };

        var config = PositionManager.BuildConfig("s", opts, 0m);

        config.TrailingStop.Method.Should().Be(TrailingMethod.AtrMultiple);
        config.TrailingStop.AtrMultiple.Should().Be(2.5);
    }

    [Fact]
    public void Disabled_trailing_ignores_an_otherwise_valid_structure_method()
    {
        var opts = new PositionManagementOptions
        {
            Trailing = new TrailingOptions { Enabled = false, Method = "Structure", StructureLookbackBars = 10 },
        };

        PositionManager.BuildConfig("s", opts, 0m).TrailingStop.Method.Should().Be(TrailingMethod.None);
    }
}
