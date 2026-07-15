using FluentAssertions;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Reconcile;

namespace TradingEngine.Tests.Unit.Reconcile;

/// <summary>
/// P0.4 (F2) — the pure entry-latency analyzer joins proposals→fills on OrderId and quantifies the
/// proposal→fill delay in seconds and decision-timeframe bars. These tests pin the math on the exact
/// audited March EURUSD H1 pair (tape fills next-M1-open after the H1 close; cTrader fills one full H1
/// bar later) so the credential-free measurement reproduces the audit-DB numbers.
/// </summary>
public sealed class EntryLatencyAnalyzerTests
{
    private static readonly DateTime P1 = new(2026, 3, 5, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime P2 = new(2026, 3, 5, 15, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime P3 = new(2026, 3, 6, 12, 0, 0, DateTimeKind.Utc);

    private static Guid Oid(int n) => Guid.Parse($"{n:D8}-0000-0000-0000-000000000000");

    private static EntryLatencyProposal Prop(int n, DateTime at) =>
        new(Oid(n), at, Timeframe.H1);

    [Fact]
    public void Tape_march_pair_delay_is_one_bar_plus_one_m1()
    {
        var proposals = new[] { Prop(1, P1), Prop(2, P2), Prop(3, P3) };
        var fills = new[]
        {
            new EntryLatencyFill(Oid(1), new DateTime(2026, 3, 5, 7, 1, 0, DateTimeKind.Utc)),
            new EntryLatencyFill(Oid(2), new DateTime(2026, 3, 5, 16, 1, 0, DateTimeKind.Utc)),
            new EntryLatencyFill(Oid(3), new DateTime(2026, 3, 6, 13, 1, 0, DateTimeKind.Utc)),
        };

        var report = EntryLatencyAnalyzer.Analyze(proposals, fills);

        report.MatchedTrades.Should().Be(3);
        report.UnmatchedFills.Should().Be(0);
        // 06:00 → 07:01 = 3660s = 1 H1 decision bar (3600s) + 1 M1 bar (60s, the HonestFills next-M1-open).
        report.DelaySeconds.Median.Should().Be(3660);
        report.DelaySeconds.Min.Should().Be(3660);
        report.DelaySeconds.Max.Should().Be(3660);
        report.DelayBars.Median.Should().BeApproximately(3660d / 3600d, 1e-9);
    }

    [Fact]
    public void Ctrader_march_pair_fills_one_full_decision_bar_later()
    {
        var proposals = new[] { Prop(1, P1), Prop(2, P2), Prop(3, P3) };
        var fills = new[]
        {
            new EntryLatencyFill(Oid(1), new DateTime(2026, 3, 5, 8, 0, 0, DateTimeKind.Utc)),
            new EntryLatencyFill(Oid(2), new DateTime(2026, 3, 5, 17, 0, 0, DateTimeKind.Utc)),
            new EntryLatencyFill(Oid(3), new DateTime(2026, 3, 6, 14, 0, 0, DateTimeKind.Utc)),
        };

        var report = EntryLatencyAnalyzer.Analyze(proposals, fills);

        report.MatchedTrades.Should().Be(3);
        // 06:00 → 08:00 = 7200s = exactly 2 H1 bars → one full decision bar later than tape's 3660s.
        report.DelaySeconds.Median.Should().Be(7200);
        report.DelayBars.Median.Should().BeApproximately(2.0, 1e-9);
    }

    [Fact]
    public void Timezone_suffix_mismatch_does_not_shift_the_delta()
    {
        // The cTrader path stamps proposal times with a trailing 'Z' (Kind=Utc); the fill entity is
        // Kind=Unspecified UTC wall-clock. The delta must be on ticks — same wall-clock ⇒ same delay.
        var proposals = new[] { new EntryLatencyProposal(Oid(1), P1, Timeframe.H1) };
        var fills = new[]
        {
            new EntryLatencyFill(Oid(1), new DateTime(2026, 3, 5, 8, 0, 0, DateTimeKind.Unspecified)),
        };

        var report = EntryLatencyAnalyzer.Analyze(proposals, fills);

        report.DelaySeconds.Median.Should().Be(7200);
    }

    [Fact]
    public void Rejected_proposals_and_orphan_fills_are_handled()
    {
        // Proposal 2 is a rejected/never-filled proposal (no fill) → dropped from the join.
        // Fill 9 has no proposal (orphan) → counted as unmatched, never a bogus delay.
        var proposals = new[] { Prop(1, P1), Prop(2, P2) };
        var fills = new[]
        {
            new EntryLatencyFill(Oid(1), new DateTime(2026, 3, 5, 7, 1, 0, DateTimeKind.Utc)),
            new EntryLatencyFill(Oid(9), new DateTime(2026, 3, 5, 9, 1, 0, DateTimeKind.Utc)),
        };

        var report = EntryLatencyAnalyzer.Analyze(proposals, fills);

        report.MatchedTrades.Should().Be(1);
        report.UnmatchedFills.Should().Be(1);
        report.DelaySeconds.Median.Should().Be(3660);
    }

    [Fact]
    public void Median_and_mean_differ_on_an_even_spread()
    {
        var proposals = new[] { Prop(1, P1), Prop(2, P1), Prop(3, P1), Prop(4, P1) };
        var fills = new[]
        {
            new EntryLatencyFill(Oid(1), P1.AddSeconds(3600)),
            new EntryLatencyFill(Oid(2), P1.AddSeconds(3660)),
            new EntryLatencyFill(Oid(3), P1.AddSeconds(3720)),
            new EntryLatencyFill(Oid(4), P1.AddSeconds(7200)),
        };

        var report = EntryLatencyAnalyzer.Analyze(proposals, fills);

        report.MatchedTrades.Should().Be(4);
        report.DelaySeconds.Min.Should().Be(3600);
        report.DelaySeconds.Max.Should().Be(7200);
        report.DelaySeconds.Median.Should().Be((3660 + 3720) / 2d);
        report.DelaySeconds.Mean.Should().BeApproximately((3600 + 3660 + 3720 + 7200) / 4d, 1e-9);
    }

    [Fact]
    public void No_fills_yields_zeroed_distribution()
    {
        var report = EntryLatencyAnalyzer.Analyze(
            new[] { Prop(1, P1) }, Array.Empty<EntryLatencyFill>());

        report.MatchedTrades.Should().Be(0);
        report.DelaySeconds.Median.Should().Be(0);
        report.DelayBars.Max.Should().Be(0);
    }
}
