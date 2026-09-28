using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plannit.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccountMailBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "UserId",
                table: "EmailDispatches",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<string>(
                name: "RecipientHash",
                table: "EmailDispatches",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailDispatches_RecipientHash_SentUtc",
                table: "EmailDispatches",
                columns: new[] { "RecipientHash", "SentUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EmailDispatches_RecipientHash_SentUtc",
                table: "EmailDispatches");

            migrationBuilder.DropColumn(
                name: "RecipientHash",
                table: "EmailDispatches");

            migrationBuilder.AlterColumn<string>(
                name: "UserId",
                table: "EmailDispatches",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
