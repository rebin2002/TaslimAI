using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieSoundtrackCueWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MovieSoundtrackAudioAssetProvenance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieSoundtrackCueVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    StoredFileId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceGenerationJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssetType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    MimeType = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    AssetMetadataJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CapturedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieSoundtrackAudioAssetProvenance", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieSoundtrackAudioAssetProvenance_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieSoundtrackAudioAssetProvenance_StoredFiles_StoredFileId",
                        column: x => x.StoredFileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MovieSoundtrackCues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieActId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieSceneId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    NarrativeIntent = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Mood = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Intensity = table.Column<int>(type: "integer", nullable: false),
                    ActStartSeconds = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    SceneStartSeconds = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    TimelineStartSeconds = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    DurationSeconds = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    ApprovalState = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ApprovedVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieSoundtrackCues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieSoundtrackCues_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieSoundtrackCues_MovieActs_MovieActId",
                        column: x => x.MovieActId,
                        principalTable: "MovieActs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieSoundtrackCues_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MovieSoundtrackCues_MovieScenes_MovieSceneId",
                        column: x => x.MovieSceneId,
                        principalTable: "MovieScenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieSoundtrackCueVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieSoundtrackCueId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Label = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ArrangementIntent = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Mood = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Intensity = table.Column<int>(type: "integer", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovalState = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ReviewNote = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieSoundtrackCueVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieSoundtrackCueVersions_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieSoundtrackCueVersions_AspNetUsers_ReviewedByUserId",
                        column: x => x.ReviewedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieSoundtrackCueVersions_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieSoundtrackCueVersions_MovieSoundtrackCues_MovieSoundtr~",
                        column: x => x.MovieSoundtrackCueId,
                        principalTable: "MovieSoundtrackCues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieSoundtrackDuckingIntents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieSoundtrackCueId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetLane = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    StartOffsetSeconds = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    EndOffsetSeconds = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    DuckDecibels = table.Column<decimal>(type: "numeric(8,3)", precision: 8, scale: 3, nullable: false),
                    AttackMilliseconds = table.Column<int>(type: "integer", nullable: false),
                    ReleaseMilliseconds = table.Column<int>(type: "integer", nullable: false),
                    Rationale = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieSoundtrackDuckingIntents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieSoundtrackDuckingIntents_MovieSoundtrackCues_MovieSoun~",
                        column: x => x.MovieSoundtrackCueId,
                        principalTable: "MovieSoundtrackCues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieSoundtrackCueVersionReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieSoundtrackCueVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Decision = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Comment = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieSoundtrackCueVersionReviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieSoundtrackCueVersionReviews_AspNetUsers_ReviewedByUser~",
                        column: x => x.ReviewedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieSoundtrackCueVersionReviews_MovieSoundtrackCueVersions~",
                        column: x => x.MovieSoundtrackCueVersionId,
                        principalTable: "MovieSoundtrackCueVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundtrackAudioAssetProvenance_AssetId",
                table: "MovieSoundtrackAudioAssetProvenance",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundtrackAudioAssetProvenance_MovieSoundtrackCueVersi~",
                table: "MovieSoundtrackAudioAssetProvenance",
                column: "MovieSoundtrackCueVersionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundtrackAudioAssetProvenance_StoredFileId",
                table: "MovieSoundtrackAudioAssetProvenance",
                column: "StoredFileId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundtrackCues_ApprovedVersionId",
                table: "MovieSoundtrackCues",
                column: "ApprovedVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundtrackCues_CreatedByUserId",
                table: "MovieSoundtrackCues",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundtrackCues_MovieActId",
                table: "MovieSoundtrackCues",
                column: "MovieActId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundtrackCues_MovieProjectId_Sequence",
                table: "MovieSoundtrackCues",
                columns: new[] { "MovieProjectId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundtrackCues_MovieProjectId_TimelineStartSeconds",
                table: "MovieSoundtrackCues",
                columns: new[] { "MovieProjectId", "TimelineStartSeconds" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundtrackCues_MovieSceneId_SceneStartSeconds",
                table: "MovieSoundtrackCues",
                columns: new[] { "MovieSceneId", "SceneStartSeconds" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundtrackCueVersionReviews_MovieSoundtrackCueVersionI~",
                table: "MovieSoundtrackCueVersionReviews",
                columns: new[] { "MovieSoundtrackCueVersionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundtrackCueVersionReviews_ReviewedByUserId",
                table: "MovieSoundtrackCueVersionReviews",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundtrackCueVersions_AssetId",
                table: "MovieSoundtrackCueVersions",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundtrackCueVersions_CreatedByUserId",
                table: "MovieSoundtrackCueVersions",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundtrackCueVersions_MovieSoundtrackCueId_ApprovalSta~",
                table: "MovieSoundtrackCueVersions",
                columns: new[] { "MovieSoundtrackCueId", "ApprovalState" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundtrackCueVersions_MovieSoundtrackCueId_VersionNumb~",
                table: "MovieSoundtrackCueVersions",
                columns: new[] { "MovieSoundtrackCueId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundtrackCueVersions_ReviewedByUserId",
                table: "MovieSoundtrackCueVersions",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundtrackDuckingIntents_MovieSoundtrackCueId_StartOff~",
                table: "MovieSoundtrackDuckingIntents",
                columns: new[] { "MovieSoundtrackCueId", "StartOffsetSeconds" });

            migrationBuilder.AddForeignKey(
                name: "FK_MovieSoundtrackAudioAssetProvenance_MovieSoundtrackCueVersi~",
                table: "MovieSoundtrackAudioAssetProvenance",
                column: "MovieSoundtrackCueVersionId",
                principalTable: "MovieSoundtrackCueVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_MovieSoundtrackCues_MovieSoundtrackCueVersions_ApprovedVers~",
                table: "MovieSoundtrackCues",
                column: "ApprovedVersionId",
                principalTable: "MovieSoundtrackCueVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MovieSoundtrackCues_MovieSoundtrackCueVersions_ApprovedVers~",
                table: "MovieSoundtrackCues");

            migrationBuilder.DropTable(
                name: "MovieSoundtrackAudioAssetProvenance");

            migrationBuilder.DropTable(
                name: "MovieSoundtrackCueVersionReviews");

            migrationBuilder.DropTable(
                name: "MovieSoundtrackDuckingIntents");

            migrationBuilder.DropTable(
                name: "MovieSoundtrackCueVersions");

            migrationBuilder.DropTable(
                name: "MovieSoundtrackCues");
        }
    }
}
