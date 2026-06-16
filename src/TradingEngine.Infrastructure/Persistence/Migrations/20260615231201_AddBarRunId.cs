using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingEngine.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBarRunId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Bars_Symbol_Timeframe_OpenTimeUtc",
                table: "Bars");

            migrationBuilder.AddColumn<string>(
                name: "RunId",
                table: "Bars",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Bars_RunId_Symbol_Timeframe_OpenTimeUtc",
                table: "Bars",
                columns: new[] { "RunId", "Symbol", "Timeframe", "OpenTimeUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Bars_RunId_Symbol_Timeframe_OpenTimeUtc",
                table: "Bars");

            migrationBuilder.DropColumn(
                name: "RunId",
                table: "Bars");

            migrationBuilder.CreateIndex(
                name: "IX_Bars_Symbol_Timeframe_OpenTimeUtc",
                table: "Bars",
                columns: new[] { "Symbol", "Timeframe", "OpenTimeUtc" });
        }
    }
}
