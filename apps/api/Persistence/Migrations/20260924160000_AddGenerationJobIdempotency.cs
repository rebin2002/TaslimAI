using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Taslim.Api.Persistence;

#nullable disable

namespace Taslim.Api.Persistence.Migrations;

[DbContext(typeof(TaslimDbContext))]
[Migration("20260924160000_AddGenerationJobIdempotency")]
public partial class AddGenerationJobIdempotency : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "IdempotencyKey",
            table: "GenerationJobs",
            type: "character varying(80)",
            maxLength: 80,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "RequestFingerprint",
            table: "GenerationJobs",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_GenerationJobs_CreatedByUserId_IdempotencyKey",
            table: "GenerationJobs",
            columns: new[] { "CreatedByUserId", "IdempotencyKey" },
            unique: true,
            filter: "\"IdempotencyKey\" IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_GenerationJobs_CreatedByUserId_IdempotencyKey",
            table: "GenerationJobs");

        migrationBuilder.DropColumn(
            name: "IdempotencyKey",
            table: "GenerationJobs");

        migrationBuilder.DropColumn(
            name: "RequestFingerprint",
            table: "GenerationJobs");
    }
}
