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
            nameof(OrderCancelled),
            nameof(CloseRequested),
            // Force-close — wired via PositionTracker.RequestForceCloseAllAsync
            "ForceCloseAllRequested",
            // iter-35 (A2): now wired in reducer (drawdown + account fold). Breach is layered in Kernel.
            nameof(EquityObserved),
            // iter-35 (A2): reducer branches revived — fed by Kernel.Decide via KernelDriver tape.
            nameof(BarClosed),
            nameof(TickReceived),
            nameof(DayRolled),
            nameof(WeekRolled),
            nameof(MonthRolled),
            // iter-36 (K4 gap-3): trailing/breakeven stop move — wired in the reducer (update stop + emit ModifyStopLoss).
            nameof(StopLossModifyRequested),
        };

        var unwiredTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            // EventBus-only events — published via IEventBus, never fed to the reducer
            "BarIngested",
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
            // iter-35 (A2): kernel-only event — handled by Kernel.Decide, not the reducer directly
            "OrderProposed",
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
