using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingEngine.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDecisionJournalFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GuardResult",
                table: "PipelineEvents",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhaseAfter",
                table: "PipelineEvents",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhaseBefore",
                table: "PipelineEvents",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Reason",
                table: "PipelineEvents",
                type: "TEXT",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GuardResult",
                table: "PipelineEvents");

            migrationBuilder.DropColumn(
                name: "PhaseAfter",
                table: "PipelineEvents");

            migrationBuilder.DropColumn(
                name: "PhaseBefore",
                table: "PipelineEvents");

            migrationBuilder.DropColumn(
                name: "Reason",
                table: "PipelineEvents");
        }
    }
}
