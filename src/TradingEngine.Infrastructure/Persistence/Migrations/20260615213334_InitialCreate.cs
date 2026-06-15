using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingEngine.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BacktestRuns",
                columns: table => new
                {
                    RunId = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    Period = table.Column<string>(type: "TEXT", nullable: false),
                    BacktestFrom = table.Column<DateTime>(type: "TEXT", nullable: false),
                    BacktestTo = table.Column<DateTime>(type: "TEXT", nullable: false),
                    InitialBalance = table.Column<decimal>(type: "TEXT", nullable: false),
                    AlgoHash = table.Column<string>(type: "TEXT", nullable: false),
                    StrategyParamsJson = table.Column<string>(type: "TEXT", nullable: false),
                    NetProfit = table.Column<decimal>(type: "TEXT", nullable: false),
                    MaxDrawdownPct = table.Column<decimal>(type: "TEXT", nullable: false),
                    TotalTrades = table.Column<int>(type: "INTEGER", nullable: false),
                    WinningTrades = table.Column<int>(type: "INTEGER", nullable: false),
                    WinRatePct = table.Column<double>(type: "REAL", nullable: false),
                    ExitCode = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    ReportJsonPath = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BacktestRuns", x => x.RunId);
                });

            migrationBuilder.CreateTable(
                name: "BarEvaluations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<string>(type: "TEXT", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    Timeframe = table.Column<string>(type: "TEXT", nullable: false),
                    BarOpenTimeUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StrategyId = table.Column<string>(type: "TEXT", nullable: false),
                    IndicatorValuesJson = table.Column<string>(type: "TEXT", nullable: false),
                    SignalFired = table.Column<bool>(type: "INTEGER", nullable: false),
                    SignalDirection = table.Column<string>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BarEvaluations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Bars",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
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
                name: "DailyProtectionLedgers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<string>(type: "TEXT", nullable: true),
                    Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartEquity = table.Column<decimal>(type: "TEXT", nullable: false),
                    MinEquity = table.Column<decimal>(type: "TEXT", nullable: false),
                    EndEquity = table.Column<decimal>(type: "TEXT", nullable: false),
                    MaxDailyDdUsedFraction = table.Column<double>(type: "REAL", nullable: false),
                    FinalGovernorState = table.Column<string>(type: "TEXT", nullable: false),
                    BreachOccurred = table.Column<bool>(type: "INTEGER", nullable: false),
                    TradesOpened = table.Column<int>(type: "INTEGER", nullable: false),
                    TradesClosed = table.Column<int>(type: "INTEGER", nullable: false),
                    SignalsBlocked = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyProtectionLedgers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EngineEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
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
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Balance = table.Column<decimal>(type: "TEXT", nullable: false),
                    FloatingPnL = table.Column<decimal>(type: "TEXT", nullable: false),
                    Equity = table.Column<decimal>(type: "TEXT", nullable: false),
                    PeakEquity = table.Column<decimal>(type: "TEXT", nullable: false),
                    DailyStartEquity = table.Column<decimal>(type: "TEXT", nullable: false),
                    CurrentDailyDrawdown = table.Column<decimal>(type: "TEXT", nullable: false),
                    CurrentMaxDrawdown = table.Column<decimal>(type: "TEXT", nullable: false),
                    Mode = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false)
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
                    Reason = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PipelineEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<string>(type: "TEXT", nullable: false),
                    Seq = table.Column<long>(type: "INTEGER", nullable: false),
                    Stage = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SimTimeUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    WallTimeUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DetailJson = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    PhaseBefore = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    PhaseAfter = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    GuardResult = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    StrategyId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Positions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
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
                name: "TradeResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PositionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    Direction = table.Column<string>(type: "TEXT", nullable: false),
                    Lots = table.Column<decimal>(type: "TEXT", nullable: false),
                    EntryPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    ExitPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    StopLoss = table.Column<decimal>(type: "TEXT", nullable: false),
                    TakeProfit = table.Column<decimal>(type: "TEXT", nullable: true),
                    OpenedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClosedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    GrossPnLAmount = table.Column<decimal>(type: "TEXT", nullable: false),
                    GrossPnLCurrency = table.Column<string>(type: "TEXT", nullable: false),
                    CommissionAmount = table.Column<decimal>(type: "TEXT", nullable: false),
                    CommissionCurrency = table.Column<string>(type: "TEXT", nullable: false),
                    SwapAmount = table.Column<decimal>(type: "TEXT", nullable: false),
                    SwapCurrency = table.Column<string>(type: "TEXT", nullable: false),
                    NetPnLAmount = table.Column<decimal>(type: "TEXT", nullable: false),
                    NetPnLCurrency = table.Column<string>(type: "TEXT", nullable: false),
                    PnLPips = table.Column<double>(type: "REAL", nullable: false),
                    RMultiple = table.Column<double>(type: "REAL", nullable: false),
                    MaxAdverseExcursion = table.Column<double>(type: "REAL", nullable: false),
                    MaxFavorableExcursion = table.Column<double>(type: "REAL", nullable: false),
                    ExitReason = table.Column<string>(type: "TEXT", nullable: false),
                    StrategyId = table.Column<string>(type: "TEXT", nullable: false),
                    RiskProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    Mode = table.Column<string>(type: "TEXT", nullable: false),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    RunId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradeResults", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProtectionLedgerEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LedgerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    EquityAtTime = table.Column<decimal>(type: "TEXT", nullable: false),
                    DailyDdUsedFraction = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProtectionLedgerEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProtectionLedgerEntries_DailyProtectionLedgers_LedgerId",
                        column: x => x.LedgerId,
                        principalTable: "DailyProtectionLedgers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExperimentRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
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
                name: "IX_BarEvaluations_RunId",
                table: "BarEvaluations",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_BarEvaluations_RunId_StrategyId_BarOpenTimeUtc",
                table: "BarEvaluations",
                columns: new[] { "RunId", "StrategyId", "BarOpenTimeUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Bars_Symbol_Timeframe_OpenTimeUtc",
                table: "Bars",
                columns: new[] { "Symbol", "Timeframe", "OpenTimeUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DailyProtectionLedgers_Date",
                table: "DailyProtectionLedgers",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_DailyProtectionLedgers_RunId",
                table: "DailyProtectionLedgers",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_EngineEvents_EventType",
                table: "EngineEvents",
                column: "EventType");

            migrationBuilder.CreateIndex(
                name: "IX_EngineEvents_OccurredAtUtc",
                table: "EngineEvents",
                column: "OccurredAtUtc");

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
                name: "IX_Orders_State",
                table: "Orders",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineEvents_RunId_Seq",
                table: "PipelineEvents",
                columns: new[] { "RunId", "Seq" });

            migrationBuilder.CreateIndex(
                name: "IX_ProtectionLedgerEntries_AtUtc",
                table: "ProtectionLedgerEntries",
                column: "AtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ProtectionLedgerEntries_LedgerId",
                table: "ProtectionLedgerEntries",
                column: "LedgerId");

            migrationBuilder.CreateIndex(
                name: "IX_TradeResults_ClosedAtUtc",
                table: "TradeResults",
                column: "ClosedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TradeResults_StrategyId",
                table: "TradeResults",
                column: "StrategyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BacktestRuns");

            migrationBuilder.DropTable(
                name: "BarEvaluations");

            migrationBuilder.DropTable(
                name: "Bars");

            migrationBuilder.DropTable(
                name: "EngineEvents");

            migrationBuilder.DropTable(
                name: "EquitySnapshots");

            migrationBuilder.DropTable(
                name: "ExperimentRuns");

            migrationBuilder.DropTable(
                name: "Orders");

            migrationBuilder.DropTable(
                name: "PipelineEvents");

            migrationBuilder.DropTable(
                name: "Positions");

            migrationBuilder.DropTable(
                name: "ProtectionLedgerEntries");

            migrationBuilder.DropTable(
                name: "TradeResults");

            migrationBuilder.DropTable(
                name: "Experiments");

            migrationBuilder.DropTable(
                name: "DailyProtectionLedgers");
        }
    }
}
