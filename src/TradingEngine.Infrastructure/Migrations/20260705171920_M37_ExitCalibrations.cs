using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class M37_ExitCalibrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExitCalibrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StrategyId = table.Column<string>(type: "TEXT", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    EntryTimeframe = table.Column<string>(type: "TEXT", nullable: false),
                    Regime = table.Column<string>(type: "TEXT", nullable: true),
                    SlAtrMultiple = table.Column<double>(type: "REAL", nullable: false),
                    TpRrMultiple = table.Column<double>(type: "REAL", nullable: true),
                    BeTriggerR = table.Column<double>(type: "REAL", nullable: true),
                    BeOffsetPips = table.Column<double>(type: "REAL", nullable: true),
                    TrailAtrMultiple = table.Column<double>(type: "REAL", nullable: true),
                    PartialTriggerR = table.Column<double>(type: "REAL", nullable: true),
                    PartialCloseFraction = table.Column<double>(type: "REAL", nullable: true),
                    DatasetId = table.Column<string>(type: "TEXT", nullable: false),
                    IsStartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsEndUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    OosStartUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    OosEndUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FittedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExitCalibrations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReferenceScales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    EntryTimeframe = table.Column<string>(type: "TEXT", nullable: false),
                    MedianAtrPips = table.Column<double>(type: "REAL", nullable: false),
                    MedianBarRangePips = table.Column<double>(type: "REAL", nullable: false),
                    MedianSpreadPips = table.Column<double>(type: "REAL", nullable: false),
                    SampleBarCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RefreshedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferenceScales", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExitCalibrations_StrategyId_Symbol_EntryTimeframe_Regime",
                table: "ExitCalibrations",
                columns: new[] { "StrategyId", "Symbol", "EntryTimeframe", "Regime" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReferenceScales_Symbol_EntryTimeframe",
                table: "ReferenceScales",
                columns: new[] { "Symbol", "EntryTimeframe" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExitCalibrations");

            migrationBuilder.DropTable(
                name: "ReferenceScales");
        }
    }
}
