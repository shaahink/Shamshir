using System.Text.Json;

namespace TradingEngine.Services;

public sealed class EffectiveConfigResolver
{
    public EffectiveConfigEntry Resolve(
        StrategyConfigEntry storedDefault,
        StrategyOverride? perRunOverride)
    {
        var id = perRunOverride?.StrategyId ?? storedDefault.Id;
        var displayName = storedDefault.DisplayName;
        var enabled = perRunOverride?.Enabled ?? storedDefault.Enabled;
        var riskProfileId = perRunOverride?.RiskProfileId ?? storedDefault.RiskProfileId;

        var parameters = MergeParameters(storedDefault.Parameters, perRunOverride?.Parameters);

        var positionManagement = MergePositionManagement(
            storedDefault.PositionManagement, perRunOverride?.PositionManagement);

        var orderEntry = MergeOrderEntry(
            storedDefault.OrderEntry, perRunOverride?.OrderEntry);

        var regimeFilter = perRunOverride?.RegimeFilter ?? storedDefault.RegimeFilter;
        var reentry = perRunOverride?.Reentry ?? storedDefault.Reentry;

        return new EffectiveConfigEntry(
            id,
            displayName,
            enabled,
            riskProfileId,
            parameters,
            positionManagement,
            orderEntry,
            regimeFilter,
            reentry);
    }

    private static JsonElement MergeParameters(JsonElement stored, JsonElement? overrideParams)
    {
        if (overrideParams is null || overrideParams.Value.ValueKind == JsonValueKind.Undefined)
            return stored;

        if (stored.ValueKind != JsonValueKind.Object || overrideParams.Value.ValueKind != JsonValueKind.Object)
            return overrideParams.Value;

        var merged = ShallowCloneObject(stored);
        foreach (var prop in overrideParams.Value.EnumerateObject())
        {
            merged[prop.Name] = prop.Value;
        }

        return SerializeObject(merged);
    }

    private static Dictionary<string, JsonElement> ShallowCloneObject(JsonElement source)
    {
        var dict = new Dictionary<string, JsonElement>();
        if (source.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in source.EnumerateObject())
                dict[prop.Name] = prop.Value;
        }
        return dict;
    }

    private static JsonElement SerializeObject(Dictionary<string, JsonElement> dict)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(dict);
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.Clone();
    }

    private static PositionManagementOptions? MergePositionManagement(
        PositionManagementOptions? stored, PositionManagementOptions? overrideOpts)
    {
        if (overrideOpts is null) return stored;
        if (stored is null) return overrideOpts;

        return new PositionManagementOptions
        {
            StopLoss = MergeSl(stored.StopLoss, overrideOpts.StopLoss),
            TakeProfit = MergeTp(stored.TakeProfit, overrideOpts.TakeProfit),
            Breakeven = MergeBreakeven(stored.Breakeven, overrideOpts.Breakeven),
            Trailing = MergeTrailing(stored.Trailing, overrideOpts.Trailing),
            Ride = overrideOpts.Ride ?? stored.Ride,
            PartialTp = overrideOpts.PartialTp ?? stored.PartialTp,
            DynamicSlTp = overrideOpts.DynamicSlTp ?? stored.DynamicSlTp,   // iter-38 A6
        };
    }

    // iter-38 (Stream PK2 / owner decision D1). Apply a reusable add-on PACK over a strategy's own add-ons:
    // the pack REPLACES the enrichments (breakeven/trailing/partial/ride/dynamic) but the mandatory baseline
    // SL/TP stays from the strategy (D4). Pack null ⇒ the strategy's own add-ons stand; both null ⇒ baseline
    // only. Call this in the run path (PK3, BacktestOrchestrator) BEFORE the per-run field override merge.
    public PositionManagementOptions? ApplyPack(PositionManagementOptions? strategyAddOns, AddOnPack? pack)
    {
        if (pack is null) return strategyAddOns;
        var baseline = strategyAddOns ?? new PositionManagementOptions();
        return baseline with
        {
            Breakeven = pack.AddOns.Breakeven,
            Trailing = pack.AddOns.Trailing,
            PartialTp = pack.AddOns.PartialTp,
            Ride = pack.AddOns.Ride,
            DynamicSlTp = pack.AddOns.DynamicSlTp,
            // StopLoss / TakeProfit (baseline) deliberately kept from the strategy.
        };
    }

    // iter-redesign P3.2: strip ALL add-ons so a "raw" / "bare" run runs the strategy's baseline SL/TP with
    // zero enrichment — the owner's explicit "no add-ons, watch the drawdown" mode. Every add-on field is
    // forced to its disabled/inactive default; only the mandatory SL/TP baseline is preserved from the input.
    public static PositionManagementOptions StripAddOns(PositionManagementOptions? input) => new()
    {
        StopLoss = input?.StopLoss ?? new(),
        TakeProfit = input?.TakeProfit ?? new(),
        // Breakeven / Trailing / Ride / PartialTp / DynamicSlTp all default to off/disabled.
    };

    // P3.2: exploration preset — widest SL (ATR×4), no TP, no exit enrichments, for the owner's
    // "utilize MAE/MFE automatically" workflow (P3.3 ExitReplayer). After stripping add-ons and
    // overriding SL/TP, the strategy runs with nothing but its entry signal — the bare excursion
    // path is the raw measure of entry quality that the exit lab then optimises over.
    public static PositionManagementOptions ApplyExplorationPreset(PositionManagementOptions? input) => new()
    {
        StopLoss = new SlOptions
        {
            Method = "AtrMultiple",
            AtrMultiple = 4.0,
            MaxPips = input?.StopLoss?.MaxPips ?? 300,
            MaxSlAtrMultiple = input?.StopLoss?.MaxSlAtrMultiple,
        },
        TakeProfit = new TpOptions
        {
            Method = "None",        // no TP — let the entry ride; exits handled by flatten or trail only
            RrMultiple = 0,
        },
        // Breakeven / Trailing / Ride / PartialTp / DynamicSlTp default to off — exploration mode
        // runs the entry signal bare so the P3.3 ExitReplayer can calibrate exits from the raw path.
    };

    private static SlOptions MergeSl(SlOptions stored, SlOptions overrideOpts)
    {
        return new SlOptions
        {
            Method = OverrideString(stored.Method, overrideOpts.Method, "AtrMultiple"),
            AtrMultiple = OverrideDouble(stored.AtrMultiple, overrideOpts.AtrMultiple, 1.5),
            FixedPips = OverrideDouble(stored.FixedPips, overrideOpts.FixedPips, 0),
            MaxPips = OverrideDouble(stored.MaxPips, overrideOpts.MaxPips, 100),
        };
    }

    private static TpOptions MergeTp(TpOptions stored, TpOptions overrideOpts)
    {
        return new TpOptions
        {
            Method = OverrideString(stored.Method, overrideOpts.Method, "RrMultiple"),
            RrMultiple = OverrideDouble(stored.RrMultiple, overrideOpts.RrMultiple, 2.0),
            FixedPips = OverrideDouble(stored.FixedPips, overrideOpts.FixedPips, 0),
            AtrMultiple = OverrideDouble(stored.AtrMultiple, overrideOpts.AtrMultiple, 0),
        };
    }

    private static BreakevenOptions MergeBreakeven(BreakevenOptions stored, BreakevenOptions overrideOpts)
    {
        return new BreakevenOptions
        {
            Enabled = OverrideBool(stored.Enabled, overrideOpts.Enabled, false),
            Mode = OverrideMode(stored.Mode, overrideOpts.Mode),   // iter-38 A1
            TriggerRMultiple = OverrideDouble(stored.TriggerRMultiple, overrideOpts.TriggerRMultiple, 1.0),
            OffsetPips = OverrideDouble(stored.OffsetPips, overrideOpts.OffsetPips, 1.0),
        };
    }

    private static TrailingOptions MergeTrailing(TrailingOptions stored, TrailingOptions overrideOpts)
    {
        return new TrailingOptions
        {
            Enabled = OverrideBool(stored.Enabled, overrideOpts.Enabled, false),   // iter-38 A1
            Mode = OverrideMode(stored.Mode, overrideOpts.Mode),
            Method = OverrideString(stored.Method, overrideOpts.Method, "None"),
            StepPips = OverrideDouble(stored.StepPips, overrideOpts.StepPips, 10),
            AtrMultiple = OverrideDouble(stored.AtrMultiple, overrideOpts.AtrMultiple, 1.0),
            ActivateAfterBreakeven = OverrideBool(stored.ActivateAfterBreakeven, overrideOpts.ActivateAfterBreakeven, true),
            StructureLookbackBars = OverrideInt(stored.StructureLookbackBars, overrideOpts.StructureLookbackBars, 10),
            SteppedRLevels = overrideOpts.SteppedRLevels.Length > 0 ? overrideOpts.SteppedRLevels : stored.SteppedRLevels,
        };
    }

    private static OrderEntryOptions? MergeOrderEntry(
        OrderEntryOptions? stored, OrderEntryOptions? overrideOpts)
    {
        if (overrideOpts is null) return stored;
        if (stored is null) return overrideOpts;

        return new OrderEntryOptions
        {
            Method = overrideOpts.Method,
            LimitOffsetPips = OverrideDouble(stored.LimitOffsetPips, overrideOpts.LimitOffsetPips, 0),
            MaxSlippagePips = OverrideDouble(stored.MaxSlippagePips, overrideOpts.MaxSlippagePips, 2.0),
            LimitOrderExpiryBars = OverrideInt(stored.LimitOrderExpiryBars, overrideOpts.LimitOrderExpiryBars, 3),
            MaxMarketRetries = OverrideInt(stored.MaxMarketRetries, overrideOpts.MaxMarketRetries, 2),
        };
    }

    private static bool OverrideBool(bool stored, bool overrideVal, bool defaultVal) =>
        overrideVal != defaultVal ? overrideVal : stored;

    private static double OverrideDouble(double stored, double overrideVal, double defaultVal) =>
        Math.Abs(overrideVal - defaultVal) > 1e-9 ? overrideVal : stored;

    private static int OverrideInt(int stored, int overrideVal, int defaultVal) =>
        overrideVal != defaultVal ? overrideVal : stored;

    private static string OverrideString(string stored, string overrideVal, string defaultVal) =>
        overrideVal != defaultVal ? overrideVal : stored;

    // iter-38 A1: add-on Mode defaults to Auto; an override of Custom wins, Auto leaves the stored value.
    private static AddOnMode OverrideMode(AddOnMode stored, AddOnMode overrideVal) =>
        overrideVal != AddOnMode.Auto ? overrideVal : stored;
}
