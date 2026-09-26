using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations;

public partial class AddMovieSelectiveRegeneration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "MovieProductionVersionId",
            table: "MovieTakes",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "MovieRegenerationRequests",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                MovieShotId = table.Column<Guid>(type: "uuid", nullable: false),
                TargetType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                ActionType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                RequestedStage = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                SourceVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                ChangedInputsJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                CompositionJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                EstimatedProviderCostUsd = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                EstimatedProviderCostKnown = table.Column<bool>(type: "boolean", nullable: false),
                CostEstimateJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                ConfirmedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                GenerationJobId = table.Column<Guid>(type: "uuid", nullable: true),
                ResultingProductionVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                ResultingTakeId = table.Column<Guid>(type: "uuid", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MovieRegenerationRequests", x => x.Id);
                table.ForeignKey("FK_MovieRegenerationRequests_AspNetUsers_ConfirmedByUserId", x => x.ConfirmedByUserId, "AspNetUsers", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_MovieRegenerationRequests_AspNetUsers_CreatedByUserId", x => x.CreatedByUserId, "AspNetUsers", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_MovieRegenerationRequests_GenerationJobs_GenerationJobId", x => x.GenerationJobId, "GenerationJobs", "Id", onDelete: ReferentialAction.SetNull);
                table.ForeignKey("FK_MovieRegenerationRequests_MovieProductionVersions_ResultingProductionVersionId", x => x.ResultingProductionVersionId, "MovieProductionVersions", "Id", onDelete: ReferentialAction.SetNull);
                table.ForeignKey("FK_MovieRegenerationRequests_MovieProductionVersions_SourceVersionId", x => x.SourceVersionId, "MovieProductionVersions", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_MovieRegenerationRequests_MovieTakes_ResultingTakeId", x => x.ResultingTakeId, "MovieTakes", "Id", onDelete: ReferentialAction.SetNull);
                table.ForeignKey("FK_MovieRegenerationRequests_MovieShots_MovieShotId", x => x.MovieShotId, "MovieShots", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(name: "IX_MovieRegenerationRequests_ConfirmedByUserId", table: "MovieRegenerationRequests", column: "ConfirmedByUserId");
        migrationBuilder.CreateIndex(name: "IX_MovieRegenerationRequests_CreatedByUserId", table: "MovieRegenerationRequests", column: "CreatedByUserId");
        migrationBuilder.CreateIndex(name: "IX_MovieRegenerationRequests_GenerationJobId", table: "MovieRegenerationRequests", column: "GenerationJobId");
        migrationBuilder.CreateIndex(name: "IX_MovieRegenerationRequests_MovieShotId_CreatedAt", table: "MovieRegenerationRequests", columns: new[] { "MovieShotId", "CreatedAt" });
        migrationBuilder.CreateIndex(name: "IX_MovieRegenerationRequests_ResultingProductionVersionId", table: "MovieRegenerationRequests", column: "ResultingProductionVersionId");
        migrationBuilder.CreateIndex(name: "IX_MovieRegenerationRequests_ResultingTakeId", table: "MovieRegenerationRequests", column: "ResultingTakeId");
        migrationBuilder.CreateIndex(name: "IX_MovieRegenerationRequests_SourceVersionId", table: "MovieRegenerationRequests", column: "SourceVersionId");
        migrationBuilder.CreateIndex(name: "IX_MovieTakes_MovieProductionVersionId", table: "MovieTakes", column: "MovieProductionVersionId", unique: true);
        migrationBuilder.AddForeignKey(name: "FK_MovieTakes_MovieProductionVersions_MovieProductionVersionId", table: "MovieTakes", column: "MovieProductionVersionId", principalTable: "MovieProductionVersions", principalColumn: "Id", onDelete: ReferentialAction.SetNull);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(name: "FK_MovieTakes_MovieProductionVersions_MovieProductionVersionId", table: "MovieTakes");
        migrationBuilder.DropTable(name: "MovieRegenerationRequests");
        migrationBuilder.DropIndex(name: "IX_MovieTakes_MovieProductionVersionId", table: "MovieTakes");
        migrationBuilder.DropColumn(name: "MovieProductionVersionId", table: "MovieTakes");
    }
}
