using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using TradingEngine.Web.Services;
using Xunit;

namespace TradingEngine.Tests.Integration.Api;

/// <summary>
/// iter-21 U1 contract test. Pins the JSON shape of the live <see cref="RunProgress"/> envelope so
/// that when iter-20 P7 backs it with the real kernel RunProjection, the producer can target this
/// exact shape and the Run Monitor page never moves. Serialized with the same camelCase policy the
/// SignalR hub uses (Program.cs AddSignalR().AddJsonProtocol).
/// </summary>
public sealed class RunProgressContractTests
{
    private static readonly JsonSerializerOptions HubOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static RunProgress Sample() => new(
        RunId: "abc123",
        Status: "running",
        SimTimeUtc: new DateTime(2026, 6, 15, 9, 30, 0, DateTimeKind.Utc),
        BarsProcessed: 1200,
        BarsTotal: 5000,
        Percent: 24.0,
        EtaSeconds: 42.5,
        WallElapsedMs: 13_500,
        BarsPerSec: 88.8,
        Equity: 10_250.50m,
        Balance: 10_000m,
        OpenPositions: 2,
        DailyDdPct: 0.012m,
        MaxDdPct: 0.034m,
        DistanceToDailyLimit: 0.038m,
        GovernorState: "Normal",
        GovernorReason: "ok",
        Counters: new RunCounters(10, 8, 8, 6, 2, 0));

    [Fact]
    public void Envelope_SerializesWithEveryDocumentedField_InCamelCase()
    {
        var json = JsonSerializer.Serialize(Sample(), HubOptions);
        var root = JsonNode.Parse(json)!.AsObject();

        string[] required =
        [
            "runId", "status", "simTimeUtc",
            "barsProcessed", "barsTotal", "percent", "etaSeconds",
            "wallElapsedMs", "barsPerSec",
            "equity", "balance", "openPositions",
            "dailyDdPct", "maxDdPct", "distanceToDailyLimit",
            "governorState", "governorReason",
            "counters",
            // iter-strategy-system P3: multi-pass context.
            "currentPass", "passIndex", "passTotal"
        ];

        foreach (var field in required)
            root.ContainsKey(field).Should().BeTrue($"envelope must expose '{field}' (camelCase)");
    }

    [Fact]
    public void Counters_ExposeTheFullFunnel_InCamelCase()
    {
        var json = JsonSerializer.Serialize(Sample(), HubOptions);
        var counters = JsonNode.Parse(json)!["counters"]!.AsObject();

        foreach (var field in new[] { "signals", "orders", "fills", "closes", "rejections", "breaches" })
            counters.ContainsKey(field).Should().BeTrue($"counters must expose '{field}'");

        counters["signals"]!.GetValue<int>().Should().Be(10);
        counters["breaches"]!.GetValue<int>().Should().Be(0);
    }
}
