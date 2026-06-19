using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDatasetsConfigSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConfigSetId",
                table: "BacktestRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DatasetId",
                table: "BacktestRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Seed",
                table: "BacktestRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ConfigSets",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigSets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Datasets",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    Symbols = table.Column<string>(type: "TEXT", nullable: false),
                    Timeframes = table.Column<string>(type: "TEXT", nullable: false),
                    FromUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ToUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Granularity = table.Column<string>(type: "TEXT", nullable: false),
                    RowCount = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Datasets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConfigSets_ContentHash",
                table: "ConfigSets",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_Datasets_ContentHash",
                table: "Datasets",
                column: "ContentHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConfigSets");

            migrationBuilder.DropTable(
                name: "Datasets");

            migrationBuilder.DropColumn(
                name: "ConfigSetId",
                table: "BacktestRuns");

            migrationBuilder.DropColumn(
                name: "DatasetId",
                table: "BacktestRuns");

            migrationBuilder.DropColumn(
                name: "Seed",
                table: "BacktestRuns");
        }
    }
}
