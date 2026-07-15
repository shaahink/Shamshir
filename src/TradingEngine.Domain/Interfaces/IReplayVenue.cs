namespace TradingEngine.Domain;

public interface IReplayVenue
{
    int BarCount { get; }

    /// <summary>Which exit resolution the venue actually used (iter-tape-trust T0/F8) — e.g. the
    /// tape venue's decision-TF vs ExitTimeframe answer. Null when the venue has only one
    /// resolution (default), so callers never type-sniff a concrete adapter for it.</summary>
    string? ExitResolution => null;
}
