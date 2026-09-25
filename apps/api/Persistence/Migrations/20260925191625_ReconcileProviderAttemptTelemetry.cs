using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReconcileProviderAttemptTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {

            migrationBuilder.AddColumn<string>(
                name: "Capability",
                table: "GenerationProviderAttempts",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CircuitOpen",
                table: "GenerationProviderAttempts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "GenerationProviderAttempts",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "GenerationProviderAttempts",
                type: "character varying(180)",
                maxLength: 180,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsFallback",
                table: "GenerationProviderAttempts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsRetry",
                table: "GenerationProviderAttempts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "JobConcurrencyToken",
                table: "GenerationProviderAttempts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<long>(
                name: "LatencyMs",
                table: "GenerationProviderAttempts",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PricingSnapshotJson",
                table: "GenerationProviderAttempts",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PricingVersion",
                table: "GenerationProviderAttempts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderExecutionId",
                table: "GenerationProviderAttempts",
                type: "character varying(240)",
                maxLength: 240,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "QualityControlRejected",
                table: "GenerationProviderAttempts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RateLimited",
                table: "GenerationProviderAttempts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ResultClassification",
                table: "GenerationProviderAttempts",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "RetryNumber",
                table: "GenerationProviderAttempts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SafeMetadataJson",
                table: "GenerationProviderAttempts",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TimedOut",
                table: "GenerationProviderAttempts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
                UPDATE "GenerationProviderAttempts" AS attempt
                SET "JobConcurrencyToken" = job."ConcurrencyToken",
                    "IdempotencyKey" = COALESCE(NULLIF(attempt."FinalizationKey", ''), 'generation:' || attempt."GenerationJobId"::text || ':attempt:' || attempt."AttemptNumber"::text),
                    "Capability" = job."JobType",
                    "Currency" = 'USD',
                    "ResultClassification" = INITCAP(LOWER(attempt."Status"))
                FROM "GenerationJobs" AS job
                WHERE job."Id" = attempt."GenerationJobId";

                WITH legacy AS (
                    SELECT old.*,
                           COALESCE((SELECT MAX(current."AttemptNumber") FROM "GenerationProviderAttempts" AS current WHERE current."GenerationJobId" = old."GenerationJobId"), 0) AS base_attempt,
                           ROW_NUMBER() OVER (PARTITION BY old."GenerationJobId" ORDER BY old."StartedAt", old."Id") AS sequence_number
                    FROM "ProviderAttempts" AS old
                    WHERE NOT EXISTS (
                        SELECT 1 FROM "GenerationProviderAttempts" AS current
                        WHERE current."Id" = old."Id"
                           OR current."FinalizationKey" = old."IdempotencyKey"
                           OR current."IdempotencyKey" = old."IdempotencyKey"
                    )
                )
                INSERT INTO "GenerationProviderAttempts"
                    ("Id", "GenerationJobId", "JobConcurrencyToken", "IdempotencyKey", "Capability", "AttemptNumber", "RetryNumber", "IsRetry", "IsFallback", "Provider", "Model", "ProviderExecutionId", "Status", "ResultClassification", "RateLimited", "TimedOut", "CircuitOpen", "QualityControlRejected", "EstimatedProviderCostUsd", "EstimatedProviderCostKnown", "ActualProviderCostUsd", "ActualProviderCostKnown", "CostEstimateJson", "FailureCode", "PricingVersion", "PricingSnapshotJson", "Currency", "SafeMetadataJson", "LatencyMs", "FinalizationKey", "StartedAt", "CompletedAt")
                SELECT legacy."Id", legacy."GenerationJobId", legacy."JobConcurrencyToken", legacy."IdempotencyKey", legacy."Capability", legacy.base_attempt + legacy.sequence_number,
                       CASE WHEN legacy."IsRetry" THEN GREATEST(1, legacy."AttemptNumber" - 1) ELSE 0 END, legacy."IsRetry", legacy."IsFallback", legacy."ProviderKey", NULL, NULL,
                       CASE LOWER(legacy."ResultCategory") WHEN 'success' THEN 'Succeeded' WHEN 'cancelled' THEN 'Cancelled' WHEN 'costguardrejected' THEN 'Rejected' ELSE 'Failed' END,
                       legacy."ResultCategory", LOWER(legacy."ResultCategory") LIKE '%rate%', LOWER(legacy."ResultCategory") LIKE '%timeout%', LOWER(legacy."ResultCategory") LIKE '%circuit%', FALSE,
                       legacy."EstimatedCostUsd", legacy."EstimatedCostUsd" IS NOT NULL, NULL, FALSE, NULL, legacy."ErrorCode", NULL, NULL, 'USD', NULL, legacy."LatencyMs", legacy."IdempotencyKey", legacy."StartedAt", legacy."CompletedAt"
                FROM legacy;

                DROP TABLE "ProviderAttempts";
                """);
            migrationBuilder.CreateIndex(
                name: "IX_GenerationProviderAttempts_GenerationJobId_IdempotencyKey",
                table: "GenerationProviderAttempts",
                columns: new[] { "GenerationJobId", "IdempotencyKey" });

            migrationBuilder.CreateIndex(
                name: "IX_GenerationProviderAttempts_Provider_Capability_StartedAt",
                table: "GenerationProviderAttempts",
                columns: new[] { "Provider", "Capability", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GenerationProviderAttempts_GenerationJobId_IdempotencyKey",
                table: "GenerationProviderAttempts");

            migrationBuilder.DropIndex(
                name: "IX_GenerationProviderAttempts_Provider_Capability_StartedAt",
                table: "GenerationProviderAttempts");

            migrationBuilder.DropColumn(
                name: "Capability",
                table: "GenerationProviderAttempts");

            migrationBuilder.DropColumn(
                name: "CircuitOpen",
                table: "GenerationProviderAttempts");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "GenerationProviderAttempts");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "GenerationProviderAttempts");

            migrationBuilder.DropColumn(
                name: "IsFallback",
                table: "GenerationProviderAttempts");

            migrationBuilder.DropColumn(
                name: "IsRetry",
                table: "GenerationProviderAttempts");

            migrationBuilder.DropColumn(
                name: "JobConcurrencyToken",
                table: "GenerationProviderAttempts");

            migrationBuilder.DropColumn(
                name: "LatencyMs",
                table: "GenerationProviderAttempts");

            migrationBuilder.DropColumn(
                name: "PricingSnapshotJson",
                table: "GenerationProviderAttempts");

            migrationBuilder.DropColumn(
                name: "PricingVersion",
                table: "GenerationProviderAttempts");

            migrationBuilder.DropColumn(
                name: "ProviderExecutionId",
                table: "GenerationProviderAttempts");

            migrationBuilder.DropColumn(
                name: "QualityControlRejected",
                table: "GenerationProviderAttempts");

            migrationBuilder.DropColumn(
                name: "RateLimited",
                table: "GenerationProviderAttempts");

            migrationBuilder.DropColumn(
                name: "ResultClassification",
                table: "GenerationProviderAttempts");

            migrationBuilder.DropColumn(
                name: "RetryNumber",
                table: "GenerationProviderAttempts");

            migrationBuilder.DropColumn(
                name: "SafeMetadataJson",
                table: "GenerationProviderAttempts");

            migrationBuilder.DropColumn(
                name: "TimedOut",
                table: "GenerationProviderAttempts");

            migrationBuilder.CreateTable(
                name: "ProviderAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    Capability = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    EstimatedCostUsd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    IsFallback = table.Column<bool>(type: "boolean", nullable: false),
                    IsRetry = table.Column<bool>(type: "boolean", nullable: false),
                    JobConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false),
                    LatencyMs = table.Column<long>(type: "bigint", nullable: true),
                    ProviderKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ResultCategory = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProviderAttempts_GenerationJobs_GenerationJobId",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderAttempts_GenerationJobId_IdempotencyKey",
                table: "ProviderAttempts",
                columns: new[] { "GenerationJobId", "IdempotencyKey" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderAttempts_ProviderKey_Capability_StartedAt",
                table: "ProviderAttempts",
                columns: new[] { "ProviderKey", "Capability", "StartedAt" });
        }
    }
}
