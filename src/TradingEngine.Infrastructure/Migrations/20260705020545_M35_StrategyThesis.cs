using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class M35_StrategyThesis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExpectedHoldBars",
                table: "StrategyConfigs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExpectedTradesPerWeek",
                table: "StrategyConfigs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Thesis",
                table: "StrategyConfigs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpectedHoldBars",
                table: "StrategyConfigs");

            migrationBuilder.DropColumn(
                name: "ExpectedTradesPerWeek",
                table: "StrategyConfigs");

            migrationBuilder.DropColumn(
                name: "Thesis",
                table: "StrategyConfigs");
        }
    }
}
