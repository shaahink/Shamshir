using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class M38_WalkForward : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WalkForwardJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SpecJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TotalWindows = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletedWindows = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalkForwardJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WalkForwardWindowResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WindowIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    TrainFromUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TrainToUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TestFromUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TestToUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StrategyId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    ChosenParamsJson = table.Column<string>(type: "TEXT", nullable: false),
                    TestRunId = table.Column<string>(type: "TEXT", nullable: true),
                    TestNetProfit = table.Column<decimal>(type: "TEXT", nullable: false),
                    TestTotalTrades = table.Column<int>(type: "INTEGER", nullable: false),
                    TestWinRatePct = table.Column<double>(type: "REAL", nullable: false),
                    TrialsCount = table.Column<int>(type: "INTEGER", nullable: false),
                    PlateauValue = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalkForwardWindowResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WalkForwardWindowResults_WalkForwardJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "WalkForwardJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WalkForwardWindowResults_JobId_WindowIndex",
                table: "WalkForwardWindowResults",
                columns: new[] { "JobId", "WindowIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WalkForwardWindowResults");

            migrationBuilder.DropTable(
                name: "WalkForwardJobs");
        }
    }
}
