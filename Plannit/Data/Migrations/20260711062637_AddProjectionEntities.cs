using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plannit.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectionEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProjectionScenarios",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    BirthYear = table.Column<int>(type: "INTEGER", nullable: false),
                    RetirementAge = table.Column<int>(type: "INTEGER", nullable: false),
                    LifeExpectancy = table.Column<int>(type: "INTEGER", nullable: false),
                    AnnualRetirementSpending = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    InflationRate = table.Column<decimal>(type: "decimal(5,4)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectionScenarios", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectionScenarios_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectionAccountAssumptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScenarioId = table.Column<int>(type: "INTEGER", nullable: false),
                    AccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    AnnualContribution = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    EmployerMatch = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ExpectedReturnRate = table.Column<decimal>(type: "decimal(5,4)", nullable: false),
                    ContributionEndAge = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectionAccountAssumptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectionAccountAssumptions_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectionAccountAssumptions_ProjectionScenarios_ScenarioId",
                        column: x => x.ScenarioId,
                        principalTable: "ProjectionScenarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectionAccountAssumptions_AccountId",
                table: "ProjectionAccountAssumptions",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectionAccountAssumptions_ScenarioId_AccountId",
                table: "ProjectionAccountAssumptions",
                columns: new[] { "ScenarioId", "AccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectionScenarios_UserId",
                table: "ProjectionScenarios",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectionAccountAssumptions");

            migrationBuilder.DropTable(
                name: "ProjectionScenarios");
        }
    }
}
