using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class M42_ConfigSeedHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SeededAtUtc",
                table: "StrategyConfigs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeededHash",
                table: "StrategyConfigs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SeededAtUtc",
                table: "RiskProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeededHash",
                table: "RiskProfiles",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SeededAtUtc",
                table: "StrategyConfigs");

            migrationBuilder.DropColumn(
                name: "SeededHash",
                table: "StrategyConfigs");

            migrationBuilder.DropColumn(
                name: "SeededAtUtc",
                table: "RiskProfiles");

            migrationBuilder.DropColumn(
                name: "SeededHash",
                table: "RiskProfiles");
        }
    }
}
