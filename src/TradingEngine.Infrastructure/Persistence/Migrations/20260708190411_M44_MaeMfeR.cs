using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingEngine.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M44_MaeMfeR : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "MaeR",
                table: "TradeResults",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MfeR",
                table: "TradeResults",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaeR",
                table: "TradeResults");

            migrationBuilder.DropColumn(
                name: "MfeR",
                table: "TradeResults");
        }
    }
}
