using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceCreditLedgerMovementUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CreditLedgerEntries_ReversesEntryId",
                table: "CreditLedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_CreditLedgerEntries_UsageTransactionId",
                table: "CreditLedgerEntries");

            migrationBuilder.CreateIndex(
                name: "IX_CreditLedgerEntries_ReversesEntryId",
                table: "CreditLedgerEntries",
                column: "ReversesEntryId",
                unique: true,
                filter: "\"ReversesEntryId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CreditLedgerEntries_UsageTransactionId",
                table: "CreditLedgerEntries",
                column: "UsageTransactionId",
                unique: true,
                filter: "\"UsageTransactionId\" IS NOT NULL AND \"Type\" = 'Debit'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CreditLedgerEntries_ReversesEntryId",
                table: "CreditLedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_CreditLedgerEntries_UsageTransactionId",
                table: "CreditLedgerEntries");

            migrationBuilder.CreateIndex(
                name: "IX_CreditLedgerEntries_ReversesEntryId",
                table: "CreditLedgerEntries",
                column: "ReversesEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_CreditLedgerEntries_UsageTransactionId",
                table: "CreditLedgerEntries",
                column: "UsageTransactionId");
        }
    }
}
