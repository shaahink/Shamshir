namespace TradingEngine.Domain;

/// <summary>
/// iter-38 (Stream A7). The canonical <c>EventKind</c> strings the kernel emits for add-on decisions, so the
/// StepRecord journal, the SPA F1 unified-journal filter, and the F2 per-bar "why" view all agree on one
/// vocabulary. The frontend already lists TRAIL / BREAKEVEN / PARTIAL — the backend must produce exactly these.
///
/// Producers (to be wired in Streams A3–A7):
///   • <see cref="AddOnsResolved"/> — once per position at entry, carrying the resolved add-on numbers
///     (Auto/Custom + the tuner output) so a run is self-describing and reproducible.
///   • <see cref="Breakeven"/> / <see cref="Trail"/> / <see cref="Partial"/> / <see cref="Ride"/> — per-bar
///     management actions, tagged so the journal renders a reason instead of a raw stop-move.
/// </summary>
public static class AddOnJournalKinds
{
    public const string AddOnsResolved = "ADDON_RESOLVED";
    public const string Breakeven = "BREAKEVEN";
    public const string Trail = "TRAIL";
    public const string Partial = "PARTIAL";
    public const string Ride = "RIDE";
}
