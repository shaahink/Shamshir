namespace TradingEngine.Domain;

public record PositionManagementOptions
{
    // Mandatory baseline (owner decision D4 — every strategy ALWAYS resolves a valid SL+TP).
    public SlOptions StopLoss { get; init; } = new();
    public TpOptions TakeProfit { get; init; } = new();

    // Optional add-ons (enrichments). Each carries Enabled + Mode (Auto/Custom) — iter-38 Stream A.
    public BreakevenOptions Breakeven { get; init; } = new();
    public TrailingOptions Trailing { get; init; } = new();
    public RideOptions? Ride { get; init; }
    public PartialTpOptions? PartialTp { get; init; }

    /// <summary>iter-38 add-on (A6): auto-tuned ATR SL/TP that REPLACES the baseline numbers when enabled.</summary>
    public DynamicSlTpOptions? DynamicSlTp { get; init; }
}

public record RideOptions
{
    public bool Enabled { get; init; }
    public AddOnMode Mode { get; init; } = AddOnMode.Auto;   // iter-38 Stream A
    public double AdxFloor { get; init; } = 25;
    public double RelaxedAtrMultiple { get; init; } = 3.0;
}

public record PartialTpOptions
{
    public bool Enabled { get; init; }
    public AddOnMode Mode { get; init; } = AddOnMode.Auto;   // iter-38 Stream A
    public double TriggerRMultiple { get; init; } = 1.0;
    public double CloseFraction { get; init; } = 0.5;
}

public record SlOptions
{
    public string Method { get; init; } = "AtrMultiple";
    public double AtrMultiple { get; init; } = 1.5;
    public double FixedPips { get; init; }
    public double MaxPips { get; init; } = 100;
}

public record TpOptions
{
    public string Method { get; init; } = "RrMultiple";
    public double RrMultiple { get; init; } = 2.0;
    public double FixedPips { get; init; }
    public double AtrMultiple { get; init; }
}

public record BreakevenOptions
{
    public bool Enabled { get; init; }
    public AddOnMode Mode { get; init; } = AddOnMode.Auto;   // iter-38 Stream A
    public double TriggerRMultiple { get; init; } = 1.0;
    public double OffsetPips { get; init; } = 1.0;
}

public record TrailingOptions
{
    // iter-38 Stream A: an explicit on/off (default OFF — add-ons are opt-in). NOTE for the agent: the kernel
    // (PositionManager.ComputeTrail) currently gates trailing on Method != "None", NOT on Enabled. When wiring
    // the toggle (A1/A7), treat trailing as active when Enabled && Method != "None", and set enabled=true on the
    // seeded strategies that already trail so behaviour is preserved.
    public bool Enabled { get; init; }
    public AddOnMode Mode { get; init; } = AddOnMode.Auto;
    public string Method { get; init; } = "None";
    public double StepPips { get; init; } = 10;
    public double AtrMultiple { get; init; } = 1.0;
    public bool ActivateAfterBreakeven { get; init; }
    public int StructureLookbackBars { get; init; } = 10;
    public double[] SteppedRLevels { get; init; } = [1.0, 2.0, 3.0];
}
