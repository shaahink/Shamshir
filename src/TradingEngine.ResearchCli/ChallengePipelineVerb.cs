using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingEngine.Domain;
using TradingEngine.Risk.Compliance;

namespace TradingEngine.ResearchCli;

/// <summary>
/// iter-pass-economics E1 (PR-E1-1/2/3) — the challenge-pipeline-EV objective, fully OFFLINE:
/// reads a CE(S)T-bucketed daily stream CSV (produced read-only by
/// <c>tools/research/e1_extract_streams.py</c>) + the prop-firm rule set JSONs, runs the
/// windowed MC (<see cref="ChallengePipelineSimulator"/>), and prints per-retry-policy results.
/// Deliberately does NOT speak HTTP or touch the DB — E1 is offline analysis; Lane R extracts
/// streams separately. Two-sided reporting is enforced here: the pooled daily-$ CI + MDE print
/// next to every EV (D1 law).
/// </summary>
public static class ChallengePipelineVerb
{
    private sealed record ProductDefaults(
        string[] PhaseFiles, string FundedFile, decimal Fee, double Split, bool Refund);

    private static readonly Dictionary<string, ProductDefaults> Products = new(StringComparer.OrdinalIgnoreCase)
    {
        // Live-verified 2026-07-27 (LEDGER Session 2 citations): 2-Step Swing $100k = $540 fee,
        // 80% base split, refund-on-first-payout confirmed; 1-Step $100k = $499, 90% split,
        // refund only third-party-corroborated (run --no-refund for the sensitivity).
        ["swing-2step"] = new(["ftmo-swing.json", "ftmo-verification.json"], "ftmo-swing.json", 540m, 0.80, true),
        ["standard-1step"] = new(["ftmo-1step.json"], "ftmo-1step.json", 499m, 0.90, true),
    };

    private static readonly (string Id, int MaxAttempts, int? StopAfterConsecutive)[] Policies =
    [
        ("RP-A", 1, null),
        ("RP-B", 6, null),
        ("RP-C", 6, 2),
    ];

    public static Task<int> RunAsync(CliArgs cli)
    {
        var csvPath = cli.Option("daily-csv");
        var productId = cli.Option("product");
        if (string.IsNullOrWhiteSpace(csvPath) || string.IsNullOrWhiteSpace(productId)
            || !Products.TryGetValue(productId, out var product))
        {
            Console.WriteLine(Verdict.Failing(
                VerdictField.Of("error", "usage: challenge-pipeline --daily-csv <path> --product swing-2step|standard-1step")).Render());
            return Task.FromResult(2);
        }

        var configDir = cli.Option("config-dir", Path.Combine("config", "prop-firms"));
        var policyFilter = cli.Option("policy", "all");
        var refund = cli.Flag("no-refund") ? false : product.Refund;
        var fee = ParseDecimal(cli.Option("fee"), product.Fee);
        var split = ParseDouble(cli.Option("split"), product.Split);

        var options = new ChallengePipelineMcOptions
        {
            Replicates = cli.Option("reps", 2000),
            PathTradingDays = cli.Option("path-days", 2500),
            MeanBlockTradingDays = cli.Option("mean-block", 10),
            Seed = cli.Option("seed", 20260727),
        };

        var days = LoadDailyCsv(csvPath);
        if (days.Count < 30)
        {
            Console.WriteLine(Verdict.Failing(VerdictField.Of("error", "stream-too-short"), VerdictField.Of("days", days.Count)).Render());
            return Task.FromResult(1);
        }

        var phases = product.PhaseFiles.Select(f => LoadRuleSet(Path.Combine(configDir, f))).ToList();
        var funded = LoadRuleSet(Path.Combine(configDir, product.FundedFile));

        var results = new Dictionary<string, ChallengePipelineMcResult>();
        foreach (var (id, maxAttempts, stopAfter) in Policies)
        {
            if (!string.Equals(policyFilter, "all", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(policyFilter, id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var spec = new ChallengePipelineSpec(
                productId, phases, funded, fee, split, refund, maxAttempts, stopAfter)
            {
                PayoutCycleTradingDays = cli.Option("payout-cycle", 21),
                FundedHorizonTradingDays = cli.Option("funded-horizon", 520),
            };
            results[id] = ChallengePipelineSimulator.Run(days, spec, options);
        }

        var jsonOpts = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            product = productId,
            csv = csvPath,
            tradingDays = days.Count,
            fee,
            split,
            refundOnFirstPayout = refund,
            options,
            policies = results,
        }, jsonOpts));

        // D1 two-sided law: the expectancy line prints IN the verdict, next to the EVs.
        var any = results.Values.First();
        var fields = new List<VerdictField>
        {
            VerdictField.Of("product", productId),
            VerdictField.Of("days", days.Count),
            VerdictField.Of("dailyMean", any.SourceMeanDailyNet),
            VerdictField.Of("dailyCi", $"[{any.SourceDailyNetCiLow:0.##},{any.SourceDailyNetCiHigh:0.##}]"),
            VerdictField.Of("dailyMde", any.SourceDailyNetMde),
        };
        foreach (var (id, r) in results)
        {
            fields.Add(VerdictField.Of($"ev{id}", r.MeanPipelineNet));
            fields.Add(VerdictField.Of($"pFunded{id}", r.PFunded));
        }
        Console.WriteLine(Verdict.Passing([.. fields]).Render());
        return Task.FromResult(0);
    }

    internal static List<PipelineDay> LoadDailyCsv(string path)
    {
        var days = new List<PipelineDay>();
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(',');
            days.Add(new PipelineDay(
                decimal.Parse(parts[1], CultureInfo.InvariantCulture),
                int.Parse(parts[2], CultureInfo.InvariantCulture)));
        }
        return days;
    }

    internal static PropFirmRuleSet LoadRuleSet(string path)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };
        return JsonSerializer.Deserialize<PropFirmRuleSet>(File.ReadAllText(path), options)
            ?? throw new InvalidOperationException($"Rule set failed to parse: {path}");
    }

    private static decimal ParseDecimal(string? value, decimal fallback) =>
        value is not null && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : fallback;

    private static double ParseDouble(string? value, double fallback) =>
        value is not null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
}
