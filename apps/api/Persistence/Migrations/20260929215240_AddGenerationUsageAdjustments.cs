using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGenerationUsageAdjustments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ActualRecordedAt",
                table: "UsageTransactions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BillableAt",
                table: "UsageTransactions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsBillable",
                table: "UsageTransactions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReservedAt",
                table: "UsageTransactions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ReversedAmount",
                table: "UsageTransactions",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.Sql("UPDATE \"UsageTransactions\" SET \"ReservedAt\" = \"CreatedAt\" WHERE \"ReservedAt\" IS NULL;");
            migrationBuilder.Sql("UPDATE \"UsageTransactions\" SET \"ActualRecordedAt\" = COALESCE(\"CompletedAt\", \"CreatedAt\") WHERE \"ProviderCostKnown\" = TRUE OR \"Status\" IN ('Completed', 'Failed');");
            migrationBuilder.Sql("UPDATE \"UsageTransactions\" SET \"IsBillable\" = TRUE, \"BillableAt\" = COALESCE(\"CompletedAt\", \"CreatedAt\") WHERE \"Status\" = 'Completed' AND \"ChargedAmount\" > 0;");

            migrationBuilder.CreateTable(
                name: "UsageTransactionAdjustments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UsageTransactionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AmountUsd = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageTransactionAdjustments", x => x.Id);
                    table.CheckConstraint("CK_UsageTransactionAdjustments_PositiveAmount", "\"AmountUsd\" > 0");
                    table.ForeignKey(
                        name: "FK_UsageTransactionAdjustments_UsageTransactions_UsageTransact~",
                        column: x => x.UsageTransactionId,
                        principalTable: "UsageTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UsageTransactionAdjustments_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UsageTransactionAdjustments_UsageTransactionId",
                table: "UsageTransactionAdjustments",
                column: "UsageTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_UsageTransactionAdjustments_WorkspaceId_IdempotencyKey",
                table: "UsageTransactionAdjustments",
                columns: new[] { "WorkspaceId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UsageTransactionAdjustments");

            migrationBuilder.DropColumn(
                name: "ActualRecordedAt",
                table: "UsageTransactions");

            migrationBuilder.DropColumn(
                name: "BillableAt",
                table: "UsageTransactions");

            migrationBuilder.DropColumn(
                name: "IsBillable",
                table: "UsageTransactions");

            migrationBuilder.DropColumn(
                name: "ReservedAt",
                table: "UsageTransactions");

            migrationBuilder.DropColumn(
                name: "ReversedAmount",
                table: "UsageTransactions");
        }
    }
}
