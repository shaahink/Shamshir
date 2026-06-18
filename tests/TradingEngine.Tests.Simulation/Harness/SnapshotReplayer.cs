using System.Text.Json;
using NetMQ;

namespace TradingEngine.Tests.Simulation.Harness;

public static class SnapshotReplayer
{
    public static async Task ReplayAsync(string snapshotPath, FakeCBot cbot, CancellationToken ct = default)
    {
        using var reader = new StreamReader(snapshotPath);

        var headerLine = await reader.ReadLineAsync(ct);
        if (headerLine is null) throw new InvalidOperationException("Empty or missing snapshot header.");

        var header = JsonSerializer.Deserialize<SnapshotHeader>(headerLine)
            ?? throw new InvalidOperationException("Invalid snapshot header.");

        var barsSent = 0;
        var execsSent = 0;

        await cbot.ConnectAsync();
        await cbot.HandshakeAsync(
            new[] { header.Symbol ?? "EURUSD" },
            new[] { header.Period ?? "H1" });

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            var frame = JsonSerializer.Deserialize<SnapshotFrame>(line);
            if (frame is null) continue;

            cbot.SendRawDealerFrame(frame.Json);

            if (frame.Json.Contains("\"type\":\"bar\""))
                barsSent++;
            else if (frame.Json.Contains("\"type\":\"exec\"") || frame.Json.Contains("\"type\":\"bar_result\""))
                execsSent++;
        }

        cbot.SendStatsAndGetJson();
    }
}
