using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieStoryboardProductionFunnel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProductionStage",
                table: "MovieShots",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "ShotPlan");

            migrationBuilder.CreateTable(
                name: "MovieProductionVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieShotId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Stage = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Label = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    CompositionJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    RegenerationMetadataJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    StageProvenanceJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    SourceVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    GenerationJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    FirstFrameAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastFrameAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    FirstFrameNotes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LastFrameNotes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieProductionVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieProductionVersions_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieProductionVersions_AspNetUsers_ReviewedByUserId",
                        column: x => x.ReviewedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieProductionVersions_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieProductionVersions_Assets_FirstFrameAssetId",
                        column: x => x.FirstFrameAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieProductionVersions_Assets_LastFrameAssetId",
                        column: x => x.LastFrameAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieProductionVersions_GenerationJobs_GenerationJobId",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieProductionVersions_MovieProductionVersions_SourceVersi~",
                        column: x => x.SourceVersionId,
                        principalTable: "MovieProductionVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieProductionVersions_MovieShots_MovieShotId",
                        column: x => x.MovieShotId,
                        principalTable: "MovieShots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieProductionStageTransitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieShotId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProductionVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromStage = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ToStage = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    EventType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    MetadataJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    SourceVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    GenerationJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieProductionStageTransitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieProductionStageTransitions_AspNetUsers_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieProductionStageTransitions_GenerationJobs_GenerationJo~",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieProductionStageTransitions_MovieProductionVersions_Mov~",
                        column: x => x.MovieProductionVersionId,
                        principalTable: "MovieProductionVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MovieProductionStageTransitions_MovieProductionVersions_Sou~",
                        column: x => x.SourceVersionId,
                        principalTable: "MovieProductionVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieProductionStageTransitions_MovieShots_MovieShotId",
                        column: x => x.MovieShotId,
                        principalTable: "MovieShots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieProductionVersionAssets",
                columns: table => new
                {
                    MovieProductionVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieProductionVersionAssets", x => new { x.MovieProductionVersionId, x.AssetId, x.Role });
                    table.ForeignKey(
                        name: "FK_MovieProductionVersionAssets_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieProductionVersionAssets_MovieProductionVersions_MovieP~",
                        column: x => x.MovieProductionVersionId,
                        principalTable: "MovieProductionVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionStageTransitions_ActorUserId",
                table: "MovieProductionStageTransitions",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionStageTransitions_GenerationJobId",
                table: "MovieProductionStageTransitions",
                column: "GenerationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionStageTransitions_MovieProductionVersionId",
                table: "MovieProductionStageTransitions",
                column: "MovieProductionVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionStageTransitions_MovieShotId_CreatedAt",
                table: "MovieProductionStageTransitions",
                columns: new[] { "MovieShotId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionStageTransitions_SourceVersionId",
                table: "MovieProductionStageTransitions",
                column: "SourceVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionVersionAssets_AssetId",
                table: "MovieProductionVersionAssets",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionVersions_AssetId",
                table: "MovieProductionVersions",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionVersions_CreatedByUserId",
                table: "MovieProductionVersions",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionVersions_FirstFrameAssetId",
                table: "MovieProductionVersions",
                column: "FirstFrameAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionVersions_GenerationJobId",
                table: "MovieProductionVersions",
                column: "GenerationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionVersions_LastFrameAssetId",
                table: "MovieProductionVersions",
                column: "LastFrameAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionVersions_MovieShotId_Stage_Status",
                table: "MovieProductionVersions",
                columns: new[] { "MovieShotId", "Stage", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionVersions_MovieShotId_VersionNumber",
                table: "MovieProductionVersions",
                columns: new[] { "MovieShotId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionVersions_ReviewedByUserId",
                table: "MovieProductionVersions",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionVersions_SourceVersionId",
                table: "MovieProductionVersions",
                column: "SourceVersionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MovieProductionStageTransitions");

            migrationBuilder.DropTable(
                name: "MovieProductionVersionAssets");

            migrationBuilder.DropTable(
                name: "MovieProductionVersions");

            migrationBuilder.DropColumn(
                name: "ProductionStage",
                table: "MovieShots");
        }
    }
}
