using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingEngine.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M52_RunStatusQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "QueuePosition",
                table: "BacktestRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "BacktestRuns",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_BacktestRuns_Status",
                table: "BacktestRuns",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BacktestRuns_Status",
                table: "BacktestRuns");

            migrationBuilder.DropColumn(
                name: "QueuePosition",
                table: "BacktestRuns");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "BacktestRuns");
        }
    }
}
