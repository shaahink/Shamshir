using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingEngine.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M47_SessionLabelAndEntryFilter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SessionLabel",
                table: "TradeExcursions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EntryFilterJson",
                table: "StrategyConfigs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SessionLabel",
                table: "TradeExcursions");

            migrationBuilder.DropColumn(
                name: "EntryFilterJson",
                table: "StrategyConfigs");
        }
    }
}
