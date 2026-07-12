using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plannit.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMonteCarloAndLifeEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ReturnStdDev",
                table: "ProjectionScenarios",
                type: "decimal(5,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "ProjectionEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScenarioId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Age = table.Column<int>(type: "INTEGER", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    IsRecurring = table.Column<bool>(type: "INTEGER", nullable: false),
                    EndAge = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectionEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectionEvents_ProjectionScenarios_ScenarioId",
                        column: x => x.ScenarioId,
                        principalTable: "ProjectionScenarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectionEvents_ScenarioId",
                table: "ProjectionEvents",
                column: "ScenarioId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectionEvents");

            migrationBuilder.DropColumn(
                name: "ReturnStdDev",
                table: "ProjectionScenarios");
        }
    }
}
