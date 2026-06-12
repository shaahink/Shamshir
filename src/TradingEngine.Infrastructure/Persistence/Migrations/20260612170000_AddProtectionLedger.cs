using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingEngine.Infrastructure.Persistence.Migrations
{
    public partial class AddProtectionLedger : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                    SignalsBlocked = table.Column<int>(type: "INTEGER", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyProtectionLedgers", x => x.Id);
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
                    DailyDdUsedFraction = table.Column<double>(type: "REAL", nullable: false),
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

            migrationBuilder.CreateIndex(
                name: "IX_DailyProtectionLedgers_RunId",
                table: "DailyProtectionLedgers",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_DailyProtectionLedgers_Date",
                table: "DailyProtectionLedgers",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_ProtectionLedgerEntries_LedgerId",
                table: "ProtectionLedgerEntries",
                column: "LedgerId");

            migrationBuilder.CreateIndex(
                name: "IX_ProtectionLedgerEntries_AtUtc",
                table: "ProtectionLedgerEntries",
                column: "AtUtc");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ProtectionLedgerEntries");
            migrationBuilder.DropTable(name: "DailyProtectionLedgers");
        }
    }
}
