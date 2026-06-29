using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRunMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "CommissionPerMillion",
                table: "BacktestRuns",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<bool>(
                name: "GovernorEnabled",
                table: "BacktestRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RegimeEnabled",
                table: "BacktestRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RiskProfileId",
                table: "BacktestRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RunPlanJson",
                table: "BacktestRuns",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<double>(
                name: "SpreadPips",
                table: "BacktestRuns",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "Venue",
                table: "BacktestRuns",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CommissionPerMillion",
                table: "BacktestRuns");

            migrationBuilder.DropColumn(
                name: "GovernorEnabled",
                table: "BacktestRuns");

            migrationBuilder.DropColumn(
                name: "RegimeEnabled",
                table: "BacktestRuns");

            migrationBuilder.DropColumn(
                name: "RiskProfileId",
                table: "BacktestRuns");

            migrationBuilder.DropColumn(
                name: "RunPlanJson",
                table: "BacktestRuns");

            migrationBuilder.DropColumn(
                name: "SpreadPips",
                table: "BacktestRuns");

            migrationBuilder.DropColumn(
                name: "Venue",
                table: "BacktestRuns");
        }
    }
}
