using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plannit.Data.Migrations
{
    /// <inheritdoc />
    public partial class RepairLiabilitySnapshotSigns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One-time data repair: liability (credit card) balance snapshots must be stored
            // positive per AccountConventions; earlier imports could leave them negative.
            // Replaces the old startup-time RepairLiabilitySnapshotSignsAsync sweep.
            migrationBuilder.Sql(@"
                UPDATE BalanceSnapshots
                SET Balance = ABS(Balance)
                WHERE Balance < 0
                  AND AccountId IN (SELECT Id FROM Accounts WHERE Type = 2);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Irreversible by design: the original sign of a repaired balance isn't recoverable.
        }
    }
}
