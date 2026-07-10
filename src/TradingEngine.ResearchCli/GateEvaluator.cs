using TradingEngine.Domain;

namespace TradingEngine.ResearchCli;

/// <summary>
/// P3.1 — the pure gate check behind <c>research run validate</c>. Given a run's terminal facts and a
/// gate spec ("trades &gt; 0, status = completed, no TRADES_LOST" — PLAN §6 P3.1), it returns a
/// <see cref="Verdict"/> with a machine-readable reason for every failed gate. Pure + deterministic so
/// the pipeline's pass/fail decision is unit-tested credential-free (the HTTP fetch is the only impure
/// part and stays a thin shell around this).
/// </summary>
public sealed record RunGateInput(string Status, int TotalTrades, string? WarningsJson, string? ErrorMessage);

public sealed record GateSpec
{
    /// <summary>Required terminal status, e.g. <c>completed</c>. Null = don't check.</summary>
    public string? RequireStatus { get; init; }

    /// <summary>Minimum number of persisted trades (0 = don't check).</summary>
    public int MinTrades { get; init; }

    /// <summary>Fail if ANY warning is present (completed-with-warnings is not clean).</summary>
    public bool ForbidWarnings { get; init; }

    /// <summary>Fail if any of these warning codes appears in WarningsJson (e.g. TRADES_LOST).</summary>
    public IReadOnlyList<string> ForbidWarningCodes { get; init; } = [];
}

public static class GateEvaluator
{
    public static Verdict Evaluate(RunGateInput run, GateSpec gates)
    {
        var fields = new List<VerdictField>
        {
            VerdictField.Of("status", run.Status),
            VerdictField.Of("trades", run.TotalTrades),
        };
        var failures = new List<string>();

        if (!string.IsNullOrWhiteSpace(gates.RequireStatus)
            && !string.Equals(run.Status, gates.RequireStatus, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"status!={gates.RequireStatus}");
        }

        if (run.TotalTrades < gates.MinTrades)
        {
            failures.Add($"trades<{gates.MinTrades}");
        }

        if (gates.ForbidWarnings && RunStatusResolver.HasWarnings(run.WarningsJson))
        {
            failures.Add("has-warnings");
        }

        foreach (var code in gates.ForbidWarningCodes)
        {
            if (!string.IsNullOrWhiteSpace(run.WarningsJson)
                && run.WarningsJson.Contains(code, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"warning:{code}");
            }
        }

        if (failures.Count == 0)
        {
            return Verdict.Passing([.. fields]);
        }

        fields.Add(VerdictField.Of("failed", string.Join(",", failures)));
        return Verdict.Failing([.. fields]);
    }
}
