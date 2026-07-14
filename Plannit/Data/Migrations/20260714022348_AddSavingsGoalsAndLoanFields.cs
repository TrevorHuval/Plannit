using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plannit.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSavingsGoalsAndLoanFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "InterestRate",
                table: "Accounts",
                type: "decimal(6,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MinimumPayment",
                table: "Accounts",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OriginalPrincipal",
                table: "Accounts",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SavingsGoals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    TargetAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TargetDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    LinkedAccountId = table.Column<int>(type: "INTEGER", nullable: true),
                    ManualProgress = table.Column<decimal>(type: "decimal(18,2)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavingsGoals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SavingsGoals_Accounts_LinkedAccountId",
                        column: x => x.LinkedAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SavingsGoals_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SavingsGoals_LinkedAccountId",
                table: "SavingsGoals",
                column: "LinkedAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SavingsGoals_UserId",
                table: "SavingsGoals",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SavingsGoals");

            migrationBuilder.DropColumn(
                name: "InterestRate",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "MinimumPayment",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "OriginalPrincipal",
                table: "Accounts");
        }
    }
}
