using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddJournalTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Journal",
                columns: table => new
                {
                    RunId = table.Column<string>(type: "TEXT", nullable: false),
                    Seq = table.Column<long>(type: "INTEGER", nullable: false),
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

            migrationBuilder.CreateIndex(
                name: "IX_Journal_RunId_SimTimeUtc",
                table: "Journal",
                columns: new[] { "RunId", "SimTimeUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Journal");
        }
    }
}
