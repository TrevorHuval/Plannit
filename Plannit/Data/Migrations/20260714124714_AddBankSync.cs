using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plannit.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBankSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SyncConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    AccessUrlProtected = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSyncStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSyncMessage = table.Column<string>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncConnections_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncAccountMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SyncConnectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    ExternalAccountId = table.Column<string>(type: "TEXT", nullable: false),
                    ExternalAccountName = table.Column<string>(type: "TEXT", nullable: true),
                    ExternalOrgName = table.Column<string>(type: "TEXT", nullable: true),
                    AccountId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncAccountMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncAccountMappings_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SyncAccountMappings_SyncConnections_SyncConnectionId",
                        column: x => x.SyncConnectionId,
                        principalTable: "SyncConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SyncConnectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    TransactionsImported = table.Column<int>(type: "INTEGER", nullable: false),
                    DuplicatesSkipped = table.Column<int>(type: "INTEGER", nullable: false),
                    SnapshotsUpdated = table.Column<int>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncLogs_SyncConnections_SyncConnectionId",
                        column: x => x.SyncConnectionId,
                        principalTable: "SyncConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncAccountMappings_AccountId",
                table: "SyncAccountMappings",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncAccountMappings_SyncConnectionId_ExternalAccountId",
                table: "SyncAccountMappings",
                columns: new[] { "SyncConnectionId", "ExternalAccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncConnections_UserId",
                table: "SyncConnections",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncLogs_SyncConnectionId_Utc",
                table: "SyncLogs",
                columns: new[] { "SyncConnectionId", "Utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncAccountMappings");

            migrationBuilder.DropTable(
                name: "SyncLogs");

            migrationBuilder.DropTable(
                name: "SyncConnections");
        }
    }
}
