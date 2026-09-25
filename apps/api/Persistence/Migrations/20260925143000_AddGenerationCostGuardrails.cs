using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Taslim.Api.Persistence;

#nullable disable

namespace Taslim.Api.Persistence.Migrations;

[DbContext(typeof(TaslimDbContext))]
[Migration("20260925143000_AddGenerationCostGuardrails")]
public partial class AddGenerationCostGuardrails : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "EstimatedProviderCostUsd",
            table: "GenerationJobs",
            type: "numeric(18,8)",
            precision: 18,
            scale: 8,
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "EstimatedProviderCostKnown",
            table: "GenerationJobs",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CostEstimateJson",
            table: "GenerationJobs",
            type: "character varying(8000)",
            maxLength: 8000,
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "ProviderCostKnown",
            table: "UsageTransactions",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "CostEstimateJson",
            table: "UsageTransactions",
            type: "character varying(8000)",
            maxLength: 8000,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "GenerationProviderAttempts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                GenerationJobId = table.Column<Guid>(type: "uuid", nullable: false),
                AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                Provider = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                Model = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                EstimatedProviderCostUsd = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                EstimatedProviderCostKnown = table.Column<bool>(type: "boolean", nullable: false),
                ActualProviderCostUsd = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                ActualProviderCostKnown = table.Column<bool>(type: "boolean", nullable: false),
                CostEstimateJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                FailureCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                FinalizationKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GenerationProviderAttempts", x => x.Id);
                table.ForeignKey(
                    name: "FK_GenerationProviderAttempts_GenerationJobs_GenerationJobId",
                    column: x => x.GenerationJobId,
                    principalTable: "GenerationJobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_GenerationProviderAttempts_GenerationJobId_AttemptNumber",
            table: "GenerationProviderAttempts",
            columns: new[] { "GenerationJobId", "AttemptNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_GenerationProviderAttempts_FinalizationKey",
            table: "GenerationProviderAttempts",
            column: "FinalizationKey",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "GenerationProviderAttempts");
        migrationBuilder.DropColumn(name: "EstimatedProviderCostUsd", table: "GenerationJobs");
        migrationBuilder.DropColumn(name: "EstimatedProviderCostKnown", table: "GenerationJobs");
        migrationBuilder.DropColumn(name: "CostEstimateJson", table: "GenerationJobs");
        migrationBuilder.DropColumn(name: "ProviderCostKnown", table: "UsageTransactions");
        migrationBuilder.DropColumn(name: "CostEstimateJson", table: "UsageTransactions");
    }
}
