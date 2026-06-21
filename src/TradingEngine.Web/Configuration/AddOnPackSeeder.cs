using Microsoft.Extensions.Logging;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence.Repositories;

namespace TradingEngine.Web.Configuration;

/// <summary>
/// iter-38 Stream PK1. Seeds the three reusable starter add-on packs the New-Backtest UI offers (idempotent —
/// skips if the store is already populated). Each pack's payload reuses <see cref="PositionManagementOptions"/>
/// (so a pack and a strategy share one shape), with every enabled add-on in <see cref="AddOnMode.Auto"/> so the
/// numbers are auto-tuned per symbol/timeframe at entry.
/// </summary>
public sealed class AddOnPackSeeder(IAddOnPackStore store, ILogger<AddOnPackSeeder> logger)
{
    public async Task SeedAsync(CancellationToken ct)
    {
        var existing = await store.GetAllAsync(ct);
        if (existing.Count > 0)
        {
            logger.LogInformation("Add-on pack store already has {Count} packs, skipping seed", existing.Count);
            return;
        }

        logger.LogInformation("Seeding {Count} starter add-on packs", StarterPacks.Count);
        foreach (var pack in StarterPacks)
            await store.UpsertAsync(pack, ct);
    }

    /// <summary>The seeded starter packs (also reused by tests).</summary>
    public static readonly IReadOnlyList<AddOnPack> StarterPacks =
    [
        new AddOnPack(
            "breakeven-only", "Breakeven Only",
            "Arm breakeven at the auto-tuned trigger R; no trailing or partials.",
            new PositionManagementOptions
            {
                Breakeven = new BreakevenOptions { Enabled = true, Mode = AddOnMode.Auto },
            }),

        new AddOnPack(
            "scalp-tight", "Scalp (tight)",
            "Early breakeven plus a tight step trail — lock in fast on short timeframes.",
            new PositionManagementOptions
            {
                Breakeven = new BreakevenOptions { Enabled = true, Mode = AddOnMode.Auto },
                Trailing = new TrailingOptions { Enabled = true, Mode = AddOnMode.Auto, Method = "StepPips" },
            }),

        new AddOnPack(
            "runner-aggressive", "Runner (aggressive)",
            "Breakeven, an ATR trail that relaxes while ADX is strong (Ride), and a partial take-profit.",
            new PositionManagementOptions
            {
                Breakeven = new BreakevenOptions { Enabled = true, Mode = AddOnMode.Auto },
                Trailing = new TrailingOptions { Enabled = true, Mode = AddOnMode.Auto, Method = "AtrMultiple" },
                Ride = new RideOptions { Enabled = true, Mode = AddOnMode.Auto },
                PartialTp = new PartialTpOptions { Enabled = true, Mode = AddOnMode.Auto },
            }),
    ];
}
