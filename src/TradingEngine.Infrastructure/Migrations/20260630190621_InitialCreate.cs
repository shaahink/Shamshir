using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AddOnPacks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    AddOnsJson = table.Column<string>(type: "TEXT", nullable: false),
                    RegimeDetectionEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AddOnPacks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BacktestRuns",
                columns: table => new
                {
                    RunId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    Period = table.Column<string>(type: "TEXT", nullable: false),
                    Symbols = table.Column<string>(type: "TEXT", nullable: false),
                    Periods = table.Column<string>(type: "TEXT", nullable: false),
                    BacktestFrom = table.Column<DateTime>(type: "TEXT", nullable: false),
                    BacktestTo = table.Column<DateTime>(type: "TEXT", nullable: false),
                    InitialBalance = table.Column<decimal>(type: "TEXT", nullable: false),
                    AlgoHash = table.Column<string>(type: "TEXT", nullable: false),
                    StrategyParamsJson = table.Column<string>(type: "TEXT", nullable: false),
                    EffectiveConfigJson = table.Column<string>(type: "TEXT", nullable: true),
                    NetProfit = table.Column<decimal>(type: "TEXT", nullable: false),
                    GrossPnL = table.Column<decimal>(type: "TEXT", nullable: false),
                    CommissionTotal = table.Column<decimal>(type: "TEXT", nullable: false),
                    SwapTotal = table.Column<decimal>(type: "TEXT", nullable: false),
                    MaxDrawdownPct = table.Column<decimal>(type: "TEXT", nullable: false),
                    TotalTrades = table.Column<int>(type: "INTEGER", nullable: false),
                    WinningTrades = table.Column<int>(type: "INTEGER", nullable: false),
                    WinRatePct = table.Column<double>(type: "REAL", nullable: false),
                    ExitCode = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    ReportJsonPath = table.Column<string>(type: "TEXT", nullable: true),
                    DatasetId = table.Column<string>(type: "TEXT", nullable: true),
                    ConfigSetId = table.Column<string>(type: "TEXT", nullable: true),
                    Seed = table.Column<int>(type: "INTEGER", nullable: false),
                    ParentRunId = table.Column<string>(type: "TEXT", nullable: true),
                    RunPlanJson = table.Column<string>(type: "TEXT", nullable: false),
                    Venue = table.Column<string>(type: "TEXT", nullable: true),
                    RiskProfileId = table.Column<string>(type: "TEXT", nullable: true),
                    GovernorEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    RegimeEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CommissionPerMillion = table.Column<double>(type: "REAL", nullable: false),
                    SpreadPips = table.Column<double>(type: "REAL", nullable: false),
                    WallElapsedMs = table.Column<long>(type: "INTEGER", nullable: false),
                    BarsPerSec = table.Column<double>(type: "REAL", nullable: false),
                    TotalBars = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BacktestRuns", x => x.RunId);
                });

            migrationBuilder.CreateTable(
                name: "Bars",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RunId = table.Column<string>(type: "TEXT", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    Timeframe = table.Column<string>(type: "TEXT", nullable: false),
                    OpenTimeUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Open = table.Column<decimal>(type: "TEXT", nullable: false),
                    High = table.Column<decimal>(type: "TEXT", nullable: false),
                    Low = table.Column<decimal>(type: "TEXT", nullable: false),
                    Close = table.Column<decimal>(type: "TEXT", nullable: false),
                    Volume = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bars", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConfigSets",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigSets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Datasets",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    Symbols = table.Column<string>(type: "TEXT", nullable: false),
                    Timeframes = table.Column<string>(type: "TEXT", nullable: false),
                    FromUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ToUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Granularity = table.Column<string>(type: "TEXT", nullable: false),
                    RowCount = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Datasets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EngineEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EngineEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EquitySnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Balance = table.Column<decimal>(type: "REAL", nullable: false),
                    FloatingPnL = table.Column<decimal>(type: "REAL", nullable: false),
                    Equity = table.Column<decimal>(type: "REAL", nullable: false),
                    PeakEquity = table.Column<decimal>(type: "REAL", nullable: false),
                    DailyStartEquity = table.Column<decimal>(type: "REAL", nullable: false),
                    CurrentDailyDrawdown = table.Column<decimal>(type: "REAL", nullable: false),
                    CurrentMaxDrawdown = table.Column<decimal>(type: "REAL", nullable: false),
                    Mode = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    RunId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquitySnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Experiments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Hypothesis = table.Column<string>(type: "TEXT", nullable: false),
                    SpecJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Experiments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GovernorOptions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GovernorOptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Journal",
                columns: table => new
                {
                    RunId = table.Column<string>(type: "TEXT", nullable: false),
                    Seq = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SimTimeUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EventKind = table.Column<string>(type: "TEXT", nullable: false),
                    EventJson = table.Column<string>(type: "TEXT", nullable: false),
                    EffectKinds = table.Column<string>(type: "TEXT", nullable: false),
                    EffectsJson = table.Column<string>(type: "TEXT", nullable: false),
                    RiskJson = table.Column<string>(type: "TEXT", nullable: false),
                    Regime = table.Column<string>(type: "TEXT", nullable: true),
                    DecisionReason = table.Column<string>(type: "TEXT", nullable: true),
                    VerdictsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Journal", x => new { x.RunId, x.Seq });
                });

            migrationBuilder.CreateTable(
                name: "Orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    Direction = table.Column<string>(type: "TEXT", nullable: false),
                    OrderType = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    RequestedLots = table.Column<decimal>(type: "TEXT", nullable: false),
                    FillPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    FilledLots = table.Column<decimal>(type: "TEXT", nullable: false),
                    RejectionReason = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FilledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LimitPrice = table.Column<string>(type: "TEXT", nullable: true),
                    StopLoss = table.Column<decimal>(type: "TEXT", nullable: true),
                    TakeProfit = table.Column<decimal>(type: "TEXT", nullable: true),
                    StrategyId = table.Column<string>(type: "TEXT", nullable: false),
                    RiskProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Positions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    OrderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    Direction = table.Column<string>(type: "TEXT", nullable: false),
                    Lots = table.Column<decimal>(type: "TEXT", nullable: false),
                    EntryPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    CurrentStopLoss = table.Column<decimal>(type: "TEXT", nullable: false),
                    TakeProfit = table.Column<decimal>(type: "TEXT", nullable: true),
                    OpenedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClosedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StrategyId = table.Column<string>(type: "TEXT", nullable: false),
                    ExitReason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Positions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PropFirmRuleSets",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropFirmRuleSets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RiskProfiles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RiskProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StrategyConfigs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    RiskProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    ParametersJson = table.Column<string>(type: "TEXT", nullable: false),
                    PositionManagementJson = table.Column<string>(type: "TEXT", nullable: true),
                    OrderEntryJson = table.Column<string>(type: "TEXT", nullable: true),
                    RegimeFilterJson = table.Column<string>(type: "TEXT", nullable: true),
                    ReentryJson = table.Column<string>(type: "TEXT", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrategyConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TradeResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PositionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    Direction = table.Column<string>(type: "TEXT", nullable: false),
                    Lots = table.Column<decimal>(type: "REAL", nullable: false),
                    EntryPrice = table.Column<decimal>(type: "REAL", nullable: false),
                    ExitPrice = table.Column<decimal>(type: "REAL", nullable: false),
                    StopLoss = table.Column<decimal>(type: "REAL", nullable: false),
                    TakeProfit = table.Column<decimal>(type: "REAL", nullable: true),
                    OpenedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClosedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    GrossPnLAmount = table.Column<decimal>(type: "REAL", nullable: false),
                    GrossPnLCurrency = table.Column<string>(type: "TEXT", nullable: false),
                    CommissionAmount = table.Column<decimal>(type: "REAL", nullable: false),
                    CommissionCurrency = table.Column<string>(type: "TEXT", nullable: false),
                    SwapAmount = table.Column<decimal>(type: "REAL", nullable: false),
                    SwapCurrency = table.Column<string>(type: "TEXT", nullable: false),
                    NetPnLAmount = table.Column<decimal>(type: "REAL", nullable: false),
                    NetPnLCurrency = table.Column<string>(type: "TEXT", nullable: false),
                    PnLPips = table.Column<double>(type: "REAL", nullable: false),
                    RMultiple = table.Column<double>(type: "REAL", nullable: false),
                    MaxAdverseExcursion = table.Column<double>(type: "REAL", nullable: false),
                    MaxFavorableExcursion = table.Column<double>(type: "REAL", nullable: false),
                    ExitReason = table.Column<string>(type: "TEXT", nullable: false),
                    StrategyId = table.Column<string>(type: "TEXT", nullable: false),
                    RiskProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    Mode = table.Column<string>(type: "TEXT", nullable: false),
                    OrderEntryMethod = table.Column<string>(type: "TEXT", nullable: false),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    RunId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradeResults", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VenueSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<string>(type: "TEXT", nullable: false),
                    Venue = table.Column<string>(type: "TEXT", nullable: false),
                    Event = table.Column<string>(type: "TEXT", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VenueSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExperimentRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExperimentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BacktestRunId = table.Column<string>(type: "TEXT", nullable: false),
                    VariantLabel = table.Column<string>(type: "TEXT", nullable: false),
                    FoldIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    FoldRole = table.Column<string>(type: "TEXT", nullable: false),
                    ScoreJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExperimentRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExperimentRuns_Experiments_ExperimentId",
                        column: x => x.ExperimentId,
                        principalTable: "Experiments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BacktestRuns_StartedAtUtc",
                table: "BacktestRuns",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Bars_RunId_Symbol_Timeframe_OpenTimeUtc",
                table: "Bars",
                columns: new[] { "RunId", "Symbol", "Timeframe", "OpenTimeUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ConfigSets_ContentHash",
                table: "ConfigSets",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_Datasets_ContentHash",
                table: "Datasets",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_EngineEvents_EventType",
                table: "EngineEvents",
                column: "EventType");

            migrationBuilder.CreateIndex(
                name: "IX_EngineEvents_OccurredAtUtc",
                table: "EngineEvents",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_EquitySnapshots_RunId",
                table: "EquitySnapshots",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_EquitySnapshots_TimestampUtc",
                table: "EquitySnapshots",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ExperimentRuns_BacktestRunId",
                table: "ExperimentRuns",
                column: "BacktestRunId");

            migrationBuilder.CreateIndex(
                name: "IX_ExperimentRuns_ExperimentId",
                table: "ExperimentRuns",
                column: "ExperimentId");

            migrationBuilder.CreateIndex(
                name: "IX_Journal_RunId_SimTimeUtc",
                table: "Journal",
                columns: new[] { "RunId", "SimTimeUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_State",
                table: "Orders",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_TradeResults_ClosedAtUtc",
                table: "TradeResults",
                column: "ClosedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TradeResults_RunId",
                table: "TradeResults",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_TradeResults_StrategyId",
                table: "TradeResults",
                column: "StrategyId");

            migrationBuilder.CreateIndex(
                name: "IX_VenueSessions_OccurredAtUtc",
                table: "VenueSessions",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_VenueSessions_RunId",
                table: "VenueSessions",
                column: "RunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AddOnPacks");

            migrationBuilder.DropTable(
                name: "BacktestRuns");

            migrationBuilder.DropTable(
                name: "Bars");

            migrationBuilder.DropTable(
                name: "ConfigSets");

            migrationBuilder.DropTable(
                name: "Datasets");

            migrationBuilder.DropTable(
                name: "EngineEvents");

            migrationBuilder.DropTable(
                name: "EquitySnapshots");

            migrationBuilder.DropTable(
                name: "ExperimentRuns");

            migrationBuilder.DropTable(
                name: "GovernorOptions");

            migrationBuilder.DropTable(
                name: "Journal");

            migrationBuilder.DropTable(
                name: "Orders");

            migrationBuilder.DropTable(
                name: "Positions");

            migrationBuilder.DropTable(
                name: "PropFirmRuleSets");

            migrationBuilder.DropTable(
                name: "RiskProfiles");

            migrationBuilder.DropTable(
                name: "StrategyConfigs");

            migrationBuilder.DropTable(
                name: "TradeResults");

            migrationBuilder.DropTable(
                name: "VenueSessions");

            migrationBuilder.DropTable(
                name: "Experiments");
        }
    }
}
