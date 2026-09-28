using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plannit.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAbuseControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SizeBytes",
                table: "TempUploads",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTime>(
                name: "EmailVerificationExpiresUtc",
                table: "NotificationPreferences",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmailVerificationTokenHash",
                table: "NotificationPreferences",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EmailVerifiedUtc",
                table: "NotificationPreferences",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EmailDispatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SentUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailDispatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmailDispatches_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailDispatches_SentUtc",
                table: "EmailDispatches",
                column: "SentUtc");

            migrationBuilder.CreateIndex(
                name: "IX_EmailDispatches_UserId_SentUtc",
                table: "EmailDispatches",
                columns: new[] { "UserId", "SentUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailDispatches");

            migrationBuilder.DropColumn(
                name: "SizeBytes",
                table: "TempUploads");

            migrationBuilder.DropColumn(
                name: "EmailVerificationExpiresUtc",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "EmailVerificationTokenHash",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "EmailVerifiedUtc",
                table: "NotificationPreferences");
        }
    }
}
