using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieGuideRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CurrentRevisionNumber",
                table: "MovieContinuityGuides",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTime>(
                name: "LockedAt",
                table: "MovieContinuityGuides",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LockedByUserId",
                table: "MovieContinuityGuides",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LockedRevisionNumber",
                table: "MovieContinuityGuides",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MovieGuideRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieContinuityGuideId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionNumber = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    StoryBibleJson = table.Column<string>(type: "character varying(50000)", maxLength: 50000, nullable: false),
                    CharacterBibleReferencesJson = table.Column<string>(type: "character varying(50000)", maxLength: 50000, nullable: false),
                    WorldBibleReferencesJson = table.Column<string>(type: "character varying(50000)", maxLength: 50000, nullable: false),
                    VisualBibleJson = table.Column<string>(type: "character varying(50000)", maxLength: 50000, nullable: false),
                    CinematographyBibleJson = table.Column<string>(type: "character varying(50000)", maxLength: 50000, nullable: false),
                    AudioBibleJson = table.Column<string>(type: "character varying(50000)", maxLength: 50000, nullable: false),
                    ContinuityBibleJson = table.Column<string>(type: "character varying(50000)", maxLength: 50000, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LockedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LockedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieGuideRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieGuideRevisions_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieGuideRevisions_AspNetUsers_LockedByUserId",
                        column: x => x.LockedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieGuideRevisions_MovieContinuityGuides_MovieContinuityGu~",
                        column: x => x.MovieContinuityGuideId,
                        principalTable: "MovieContinuityGuides",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MovieGuideRevisions_CreatedByUserId",
                table: "MovieGuideRevisions",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieGuideRevisions_LockedByUserId",
                table: "MovieGuideRevisions",
                column: "LockedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieGuideRevisions_MovieContinuityGuideId_RevisionNumber",
                table: "MovieGuideRevisions",
                columns: new[] { "MovieContinuityGuideId", "RevisionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieGuideRevisions_MovieContinuityGuideId_Status",
                table: "MovieGuideRevisions",
                columns: new[] { "MovieContinuityGuideId", "Status" });
            migrationBuilder.Sql("""
                INSERT INTO "MovieGuideRevisions" ("Id", "MovieContinuityGuideId", "RevisionNumber", "Status", "StoryBibleJson", "CharacterBibleReferencesJson", "WorldBibleReferencesJson", "VisualBibleJson", "CinematographyBibleJson", "AudioBibleJson", "ContinuityBibleJson", "CreatedByUserId", "CreatedAt")
                SELECT md5('taslim.movie.guide.' || guide."Id"::text)::uuid,
                       guide."Id",
                       1,
                       'Draft',
                       '{}',
                       '[]',
                       '[]',
                       json_build_object('visualLanguage', guide."VisualLanguage", 'colorAndLighting', guide."ColorAndLighting")::text,
                       json_build_object('cameraLanguage', guide."CameraLanguage")::text,
                       json_build_object('soundAndNarration', guide."SoundAndNarration")::text,
                       json_build_object('continuityRules', guide."ContinuityRules")::text,
                       movie."CreatedByUserId",
                       guide."UpdatedAt"
                FROM "MovieContinuityGuides" guide
                INNER JOIN "MovieProjects" movie ON movie."Id" = guide."MovieProjectId"
                WHERE NOT EXISTS (
                    SELECT 1 FROM "MovieGuideRevisions" existing
                    WHERE existing."MovieContinuityGuideId" = guide."Id"
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MovieGuideRevisions");

            migrationBuilder.DropColumn(
                name: "CurrentRevisionNumber",
                table: "MovieContinuityGuides");

            migrationBuilder.DropColumn(
                name: "LockedAt",
                table: "MovieContinuityGuides");

            migrationBuilder.DropColumn(
                name: "LockedByUserId",
                table: "MovieContinuityGuides");

            migrationBuilder.DropColumn(
                name: "LockedRevisionNumber",
                table: "MovieContinuityGuides");
        }
    }
}
