using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class M36_TradeExcursions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TradeExcursions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RunId = table.Column<string>(type: "TEXT", nullable: false),
                    PositionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PathJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradeExcursions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TradeExcursions_RunId",
                table: "TradeExcursions",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_TradeExcursions_RunId_PositionId",
                table: "TradeExcursions",
                columns: new[] { "RunId", "PositionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TradeExcursions");
        }
    }
}
