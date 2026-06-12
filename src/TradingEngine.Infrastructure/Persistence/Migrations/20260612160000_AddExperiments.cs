using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingEngine.Infrastructure.Persistence.Migrations
{
    public partial class AddExperiments : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Experiments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Hypothesis = table.Column<string>(type: "TEXT", nullable: false),
                    SpecJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Experiments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExperimentRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExperimentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BacktestRunId = table.Column<string>(type: "TEXT", nullable: false),
                    VariantLabel = table.Column<string>(type: "TEXT", nullable: false),
                    FoldIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    FoldRole = table.Column<string>(type: "TEXT", nullable: false),
                    ScoreJson = table.Column<string>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExperimentRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExperimentRuns_Experiments_ExperimentId",
                        column: x => x.ExperimentId,
                        principalTable: "Experiments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExperimentRuns_ExperimentId",
                table: "ExperimentRuns",
                column: "ExperimentId");

            migrationBuilder.CreateIndex(
                name: "IX_ExperimentRuns_BacktestRunId",
                table: "ExperimentRuns",
                column: "BacktestRunId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ExperimentRuns");
            migrationBuilder.DropTable(name: "Experiments");
        }
    }
}
