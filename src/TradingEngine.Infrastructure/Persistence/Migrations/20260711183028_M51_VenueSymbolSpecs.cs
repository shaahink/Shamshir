using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingEngine.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M51_VenueSymbolSpecs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VenueSymbolSpecs",
                columns: table => new
                {
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    Broker = table.Column<string>(type: "TEXT", nullable: false),
                    CapturedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    Commission = table.Column<double>(type: "REAL", nullable: false),
                    CommissionType = table.Column<string>(type: "TEXT", nullable: false),
                    SwapLong = table.Column<double>(type: "REAL", nullable: false),
                    SwapShort = table.Column<double>(type: "REAL", nullable: false),
                    SwapCalculationType = table.Column<string>(type: "TEXT", nullable: false),
                    LotSize = table.Column<double>(type: "REAL", nullable: false),
                    PipSize = table.Column<double>(type: "REAL", nullable: false),
                    TickSize = table.Column<double>(type: "REAL", nullable: false),
                    TickValue = table.Column<double>(type: "REAL", nullable: false),
                    Digits = table.Column<int>(type: "INTEGER", nullable: false),
                    TripleSwapDay = table.Column<string>(type: "TEXT", nullable: false),
                    TypicalSpread = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VenueSymbolSpecs", x => new { x.Symbol, x.Broker });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VenueSymbolSpecs");
        }
    }
}
