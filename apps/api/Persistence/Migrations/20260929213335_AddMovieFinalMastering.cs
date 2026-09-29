using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieFinalMastering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MovieProductionVersionId",
                table: "MovieTakes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContinuityReferences",
                table: "MovieShots",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocationSet",
                table: "MovieShots",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProductionRequirements",
                table: "MovieShots",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Purpose",
                table: "MovieShots",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubjectCharacterIdsJson",
                table: "MovieShots",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Subjects",
                table: "MovieShots",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CinematographyReferenceJson",
                table: "MovieProductionVersions",
                type: "character varying(20000)",
                maxLength: 20000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContinuitySnapshotHash",
                table: "MovieProductionVersions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ContinuitySnapshotId",
                table: "MovieProductionVersions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContinuitySnapshotReferenceJson",
                table: "MovieProductionVersions",
                type: "character varying(20000)",
                maxLength: 20000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ContinuitySnapshotVersion",
                table: "MovieProductionVersions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContinuitySnapshotHash",
                table: "MovieClips",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ContinuitySnapshotId",
                table: "MovieClips",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ContinuitySnapshotVersion",
                table: "MovieClips",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MovieCharacterContinuitySnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieSceneId = table.Column<Guid>(type: "uuid", nullable: true),
                    MovieShotId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    SnapshotJson = table.Column<string>(type: "character varying(60000)", maxLength: 60000, nullable: false),
                    SnapshotHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieCharacterContinuitySnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieCharacterContinuitySnapshots_MovieProjects_MovieProjec~",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MovieCharacterContinuitySnapshots_MovieScenes_MovieSceneId",
                        column: x => x.MovieSceneId,
                        principalTable: "MovieScenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MovieCharacterContinuitySnapshots_MovieShots_MovieShotId",
                        column: x => x.MovieShotId,
                        principalTable: "MovieShots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieFinalMasters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieShotId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceTakeId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceMovieClipId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    OutputAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    GenerationJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetProfile = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SourceWidth = table.Column<int>(type: "integer", nullable: true),
                    SourceHeight = table.Column<int>(type: "integer", nullable: true),
                    TargetWidth = table.Column<int>(type: "integer", nullable: false),
                    TargetHeight = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    StateReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    QcStatus = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    QcResultJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    ProvenanceJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    SupersedesMasterId = table.Column<Guid>(type: "uuid", nullable: true),
                    SupersededByMasterId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SupersededAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieFinalMasters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieFinalMasters_AspNetUsers_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieFinalMasters_Assets_OutputAssetId",
                        column: x => x.OutputAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieFinalMasters_Assets_SourceAssetId",
                        column: x => x.SourceAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieFinalMasters_GenerationJobs_GenerationJobId",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieFinalMasters_MovieClips_SourceMovieClipId",
                        column: x => x.SourceMovieClipId,
                        principalTable: "MovieClips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieFinalMasters_MovieFinalMasters_SupersedesMasterId",
                        column: x => x.SupersedesMasterId,
                        principalTable: "MovieFinalMasters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieFinalMasters_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MovieFinalMasters_MovieShots_MovieShotId",
                        column: x => x.MovieShotId,
                        principalTable: "MovieShots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MovieFinalMasters_MovieTakes_SourceTakeId",
                        column: x => x.SourceTakeId,
                        principalTable: "MovieTakes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

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
                    table.ForeignKey(
                        name: "FK_MovieRegenerationRequests_AspNetUsers_ConfirmedByUserId",
                        column: x => x.ConfirmedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieRegenerationRequests_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieRegenerationRequests_GenerationJobs_GenerationJobId",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieRegenerationRequests_MovieProductionVersions_Resulting~",
                        column: x => x.ResultingProductionVersionId,
                        principalTable: "MovieProductionVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieRegenerationRequests_MovieProductionVersions_SourceVer~",
                        column: x => x.SourceVersionId,
                        principalTable: "MovieProductionVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieRegenerationRequests_MovieShots_MovieShotId",
                        column: x => x.MovieShotId,
                        principalTable: "MovieShots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MovieRegenerationRequests_MovieTakes_ResultingTakeId",
                        column: x => x.ResultingTakeId,
                        principalTable: "MovieTakes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MovieTakes_MovieProductionVersionId",
                table: "MovieTakes",
                column: "MovieProductionVersionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieCharacterContinuitySnapshots_MovieProjectId_CreatedAt",
                table: "MovieCharacterContinuitySnapshots",
                columns: new[] { "MovieProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieCharacterContinuitySnapshots_MovieProjectId_MovieScene~",
                table: "MovieCharacterContinuitySnapshots",
                columns: new[] { "MovieProjectId", "MovieSceneId", "MovieShotId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieCharacterContinuitySnapshots_MovieSceneId",
                table: "MovieCharacterContinuitySnapshots",
                column: "MovieSceneId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieCharacterContinuitySnapshots_MovieShotId",
                table: "MovieCharacterContinuitySnapshots",
                column: "MovieShotId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieFinalMasters_GenerationJobId",
                table: "MovieFinalMasters",
                column: "GenerationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieFinalMasters_MovieProjectId",
                table: "MovieFinalMasters",
                column: "MovieProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieFinalMasters_MovieShotId_RequestedAt",
                table: "MovieFinalMasters",
                columns: new[] { "MovieShotId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieFinalMasters_OutputAssetId",
                table: "MovieFinalMasters",
                column: "OutputAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieFinalMasters_RequestedByUserId",
                table: "MovieFinalMasters",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieFinalMasters_SourceAssetId",
                table: "MovieFinalMasters",
                column: "SourceAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieFinalMasters_SourceMovieClipId",
                table: "MovieFinalMasters",
                column: "SourceMovieClipId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieFinalMasters_SourceTakeId_TargetProfile_SupersededByMa~",
                table: "MovieFinalMasters",
                columns: new[] { "SourceTakeId", "TargetProfile", "SupersededByMasterId" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieFinalMasters_SupersedesMasterId",
                table: "MovieFinalMasters",
                column: "SupersedesMasterId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieRegenerationRequests_ConfirmedByUserId",
                table: "MovieRegenerationRequests",
                column: "ConfirmedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieRegenerationRequests_CreatedByUserId",
                table: "MovieRegenerationRequests",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieRegenerationRequests_GenerationJobId",
                table: "MovieRegenerationRequests",
                column: "GenerationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieRegenerationRequests_MovieShotId_CreatedAt",
                table: "MovieRegenerationRequests",
                columns: new[] { "MovieShotId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieRegenerationRequests_ResultingProductionVersionId",
                table: "MovieRegenerationRequests",
                column: "ResultingProductionVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieRegenerationRequests_ResultingTakeId",
                table: "MovieRegenerationRequests",
                column: "ResultingTakeId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieRegenerationRequests_SourceVersionId",
                table: "MovieRegenerationRequests",
                column: "SourceVersionId");

            migrationBuilder.AddForeignKey(
                name: "FK_MovieTakes_MovieProductionVersions_MovieProductionVersionId",
                table: "MovieTakes",
                column: "MovieProductionVersionId",
                principalTable: "MovieProductionVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MovieTakes_MovieProductionVersions_MovieProductionVersionId",
                table: "MovieTakes");

            migrationBuilder.DropTable(
                name: "MovieCharacterContinuitySnapshots");

            migrationBuilder.DropTable(
                name: "MovieFinalMasters");

            migrationBuilder.DropTable(
                name: "MovieRegenerationRequests");

            migrationBuilder.DropIndex(
                name: "IX_MovieTakes_MovieProductionVersionId",
                table: "MovieTakes");

            migrationBuilder.DropColumn(
                name: "MovieProductionVersionId",
                table: "MovieTakes");

            migrationBuilder.DropColumn(
                name: "ContinuityReferences",
                table: "MovieShots");

            migrationBuilder.DropColumn(
                name: "LocationSet",
                table: "MovieShots");

            migrationBuilder.DropColumn(
                name: "ProductionRequirements",
                table: "MovieShots");

            migrationBuilder.DropColumn(
                name: "Purpose",
                table: "MovieShots");

            migrationBuilder.DropColumn(
                name: "SubjectCharacterIdsJson",
                table: "MovieShots");

            migrationBuilder.DropColumn(
                name: "Subjects",
                table: "MovieShots");

            migrationBuilder.DropColumn(
                name: "CinematographyReferenceJson",
                table: "MovieProductionVersions");

            migrationBuilder.DropColumn(
                name: "ContinuitySnapshotHash",
                table: "MovieProductionVersions");

            migrationBuilder.DropColumn(
                name: "ContinuitySnapshotId",
                table: "MovieProductionVersions");

            migrationBuilder.DropColumn(
                name: "ContinuitySnapshotReferenceJson",
                table: "MovieProductionVersions");

            migrationBuilder.DropColumn(
                name: "ContinuitySnapshotVersion",
                table: "MovieProductionVersions");

            migrationBuilder.DropColumn(
                name: "ContinuitySnapshotHash",
                table: "MovieClips");

            migrationBuilder.DropColumn(
                name: "ContinuitySnapshotId",
                table: "MovieClips");

            migrationBuilder.DropColumn(
                name: "ContinuitySnapshotVersion",
                table: "MovieClips");
        }
    }
}
