using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class M33_TradeNarrativeColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EntryReason",
                table: "TradeResults",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EntryRegime",
                table: "TradeResults",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EntrySnapshotJson",
                table: "TradeResults",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExitDetailJson",
                table: "TradeResults",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EntryReason",
                table: "TradeResults");

            migrationBuilder.DropColumn(
                name: "EntryRegime",
                table: "TradeResults");

            migrationBuilder.DropColumn(
                name: "EntrySnapshotJson",
                table: "TradeResults");

            migrationBuilder.DropColumn(
                name: "ExitDetailJson",
                table: "TradeResults");
        }
    }
}
