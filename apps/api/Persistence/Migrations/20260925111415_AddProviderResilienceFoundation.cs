using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Taslim.Api.Persistence.Migrations;

/// <inheritdoc />
public partial class AddProviderResilienceFoundation : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ProviderCircuits",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProviderKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                Capability = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                OpenedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                OpenUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                ProbeExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastFailureAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastSuccessAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                RowVersion = table.Column<Guid>(type: "uuid", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_ProviderCircuits", x => x.Id));

        migrationBuilder.CreateTable(
            name: "ProviderAttempts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                GenerationJobId = table.Column<Guid>(type: "uuid", nullable: false),
                JobConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false),
                IdempotencyKey = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                Capability = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                ProviderKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                ResultCategory = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                LatencyMs = table.Column<long>(type: "bigint", nullable: true),
                EstimatedCostUsd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                IsRetry = table.Column<bool>(type: "boolean", nullable: false),
                IsFallback = table.Column<bool>(type: "boolean", nullable: false),
                StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
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

        migrationBuilder.CreateTable(
            name: "ProviderExecutionFinalizations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                GenerationJobId = table.Column<Guid>(type: "uuid", nullable: false),
                JobConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false),
                IdempotencyKey = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                ClaimedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                ClaimExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProviderExecutionFinalizations", x => x.Id);
                table.ForeignKey(
                    name: "FK_ProviderExecutionFinalizations_GenerationJobs_GenerationJobId",
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

        migrationBuilder.CreateIndex(
            name: "IX_ProviderCircuits_ProviderKey_Capability",
            table: "ProviderCircuits",
            columns: new[] { "ProviderKey", "Capability" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ProviderCircuits_State_OpenUntil",
            table: "ProviderCircuits",
            columns: new[] { "State", "OpenUntil" });

        migrationBuilder.CreateIndex(
            name: "IX_ProviderExecutionFinalizations_GenerationJobId_IdempotencyKey",
            table: "ProviderExecutionFinalizations",
            columns: new[] { "GenerationJobId", "IdempotencyKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ProviderExecutionFinalizations_State_ClaimExpiresAt",
            table: "ProviderExecutionFinalizations",
            columns: new[] { "State", "ClaimExpiresAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ProviderExecutionFinalizations");
        migrationBuilder.DropTable(name: "ProviderAttempts");
        migrationBuilder.DropTable(name: "ProviderCircuits");
    }
}
