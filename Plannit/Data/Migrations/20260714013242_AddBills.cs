using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plannit.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Bills",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    MerchantKey = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Cadence = table.Column<int>(type: "INTEGER", nullable: false),
                    ExpectedAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    NextDue = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    IsIncome = table.Column<bool>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastPaidDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    LastPaidTransactionId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Bills_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Bills_UserId",
                table: "Bills",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Bills_UserId_MerchantKey_IsIncome",
                table: "Bills",
                columns: new[] { "UserId", "MerchantKey", "IsIncome" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Bills");
        }
    }
}
