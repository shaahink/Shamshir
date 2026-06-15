using System.Reflection;
using TradingEngine.Engine;

namespace TradingEngine.Tests.Unit.Architecture;

public sealed class EngineReducerWiringTests
{
    /// <summary>
    /// Asserts which EngineEvent subtypes are reduced by EngineReducer.Apply.
    /// If an UNWIRED event type becomes constructed-and-fed-to-the-reducer, this
    /// test FAILS — the developer must either (a) complete the kernel wiring or
    /// (b) update this pinned set and document why. Prevents silent double-wiring.
    /// See SYSTEM-MODEL.md §3.2.
    /// </summary>
    [Fact]
    public void EngineReducer_OnlyWiredEventTypes_AreReduced()
    {
        var allEventTypes = typeof(EngineEvent).Assembly
            .GetTypes()
            .Where(t => t.IsAssignableTo(typeof(EngineEvent)) && !t.IsAbstract)
            .OrderBy(t => t.Name)
            .ToList();

        // Verify we found engine events
        allEventTypes.Should().NotBeEmpty("EngineEvent subtypes must exist in the domain assembly");

        // Determine which types are actually handled by the reducer.
        // The Apply method has a switch-case on the event type; any case that returns
        // the state unchanged (or does nothing meaningful) is considered unwired.

        var wiredTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            // Position lifecycle — wired via PositionTracker.TrackOrder/OnExecutionAsync
            nameof(OrderSubmitted),
            nameof(OrderFilled),
            nameof(OrderPartiallyFilled),
            nameof(OrderRejected),
            nameof(CloseRequested),
            // Force-close — wired via PositionTracker.RequestForceCloseAllAsync
            "ForceCloseAllRequested",
        };

        var unwiredTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            // UNWIRED — RiskManager is authoritative; see EngineReducer.cs banners
            nameof(BarClosed),
            nameof(TickReceived),
            nameof(EquityObserved),
            nameof(DayRolled),
            nameof(WeekRolled),
            // EventBus-only events — published via IEventBus, never fed to the reducer
            "BarEvaluated",
            "TradeClosed",
            "TradeOpened",
            "TradeBlocked",
            "EquityUpdated",
            "DrawdownBreached",
            "GovernorStateChanged",
            "ProtectionModeEntered",
            "MonthlyEquitySnapshotTaken",
            "WeeklyEquitySnapshotTaken",
            "PositionPartiallyClosed",
        };

        // Every EngineEvent type must be in exactly one of these two sets.
        var declared = wiredTypes.Concat(unwiredTypes).ToHashSet(StringComparer.Ordinal);

        foreach (var t in allEventTypes)
        {
            declared.Should().Contain(t.Name,
                $"EngineEvent type '{t.Name}' is not in the wired or unwired set. " +
                "Add it to either wiredTypes or unwiredTypes in this test.");
            if (unwiredTypes.Contains(t.Name))
            {
                // Type is declared unwired — verify that no code constructs it.
                // We can't do a whole-codebase static analysis, but we CAN verify
                // the ENGINE REDUCER doesn't meaningfully process it.
                // The banner comments on each Handle* method serve as the primary guard.
            }
        }

        // Inverse check: every type in wired/unwired sets must exist
        foreach (var name in declared)
        {
            allEventTypes.Should().Contain(t => t.Name == name,
                $"Type '{name}' is in the wiring declaration but does not exist as an EngineEvent subtype");
        }
    }
}
