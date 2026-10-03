using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceSingleUsageAdjustment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UsageTransactionAdjustments_UsageTransactionId",
                table: "UsageTransactionAdjustments");

            migrationBuilder.CreateIndex(
                name: "IX_UsageTransactionAdjustments_UsageTransactionId",
                table: "UsageTransactionAdjustments",
                column: "UsageTransactionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UsageTransactionAdjustments_UsageTransactionId",
                table: "UsageTransactionAdjustments");

            migrationBuilder.CreateIndex(
                name: "IX_UsageTransactionAdjustments_UsageTransactionId",
                table: "UsageTransactionAdjustments",
                column: "UsageTransactionId");
        }
    }
}
