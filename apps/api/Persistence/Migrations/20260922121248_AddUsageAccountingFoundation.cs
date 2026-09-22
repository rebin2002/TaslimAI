using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUsageAccountingFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AnomalyCode",
                table: "UsageTransactions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AnomalyDetectedAt",
                table: "UsageTransactions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "UsageTransactions",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "USD");

            migrationBuilder.AddColumn<decimal>(
                name: "EstimatedProviderCostUsd",
                table: "UsageTransactions",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "GenerationJobId",
                table: "UsageTransactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImageInputTokens",
                table: "UsageTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImageOutputTokens",
                table: "UsageTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsAnomalous",
                table: "UsageTransactions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "LatencyMs",
                table: "UsageTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PricingSnapshotJson",
                table: "UsageTransactions",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PricingVersion",
                table: "UsageTransactions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RefundedAt",
                table: "UsageTransactions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SafeMetadataJson",
                table: "UsageTransactions",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_UsageTransactions_GenerationJobId_CreatedAt",
                table: "UsageTransactions",
                columns: new[] { "GenerationJobId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageTransactions_IsAnomalous",
                table: "UsageTransactions",
                column: "IsAnomalous");

            migrationBuilder.CreateIndex(
                name: "IX_UsageTransactions_Provider",
                table: "UsageTransactions",
                column: "Provider");

            migrationBuilder.AddForeignKey(
                name: "FK_UsageTransactions_GenerationJobs_GenerationJobId",
                table: "UsageTransactions",
                column: "GenerationJobId",
                principalTable: "GenerationJobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_UsageTransactions_GenerationJobs_GenerationJobId",
                table: "UsageTransactions");

            migrationBuilder.DropIndex(
                name: "IX_UsageTransactions_GenerationJobId_CreatedAt",
                table: "UsageTransactions");

            migrationBuilder.DropIndex(
                name: "IX_UsageTransactions_IsAnomalous",
                table: "UsageTransactions");

            migrationBuilder.DropIndex(
                name: "IX_UsageTransactions_Provider",
                table: "UsageTransactions");

            migrationBuilder.DropColumn(
                name: "AnomalyCode",
                table: "UsageTransactions");

            migrationBuilder.DropColumn(
                name: "AnomalyDetectedAt",
                table: "UsageTransactions");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "UsageTransactions");

            migrationBuilder.DropColumn(
                name: "EstimatedProviderCostUsd",
                table: "UsageTransactions");

            migrationBuilder.DropColumn(
                name: "GenerationJobId",
                table: "UsageTransactions");

            migrationBuilder.DropColumn(
                name: "ImageInputTokens",
                table: "UsageTransactions");

            migrationBuilder.DropColumn(
                name: "ImageOutputTokens",
                table: "UsageTransactions");

            migrationBuilder.DropColumn(
                name: "IsAnomalous",
                table: "UsageTransactions");

            migrationBuilder.DropColumn(
                name: "LatencyMs",
                table: "UsageTransactions");

            migrationBuilder.DropColumn(
                name: "PricingSnapshotJson",
                table: "UsageTransactions");

            migrationBuilder.DropColumn(
                name: "PricingVersion",
                table: "UsageTransactions");

            migrationBuilder.DropColumn(
                name: "RefundedAt",
                table: "UsageTransactions");

            migrationBuilder.DropColumn(
                name: "SafeMetadataJson",
                table: "UsageTransactions");
        }
    }
}
