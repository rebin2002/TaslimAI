using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieStoryScreenplayFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MovieStories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Premise = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Logline = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Synopsis = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    Treatment = table.Column<string>(type: "character varying(40000)", maxLength: 40000, nullable: false),
                    ApprovalState = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CurrentRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieStories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieStories_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieStoryRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieStoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionNumber = table.Column<int>(type: "integer", nullable: false),
                    Premise = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Logline = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Synopsis = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    Treatment = table.Column<string>(type: "character varying(40000)", maxLength: 40000, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Authorship = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ChangeSummary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieStoryRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieStoryRevisions_MovieStories_MovieStoryId",
                        column: x => x.MovieStoryId,
                        principalTable: "MovieStories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieScreenplayScenes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieStoryRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieSceneId = table.Column<Guid>(type: "uuid", nullable: true),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    SceneIdentifier = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ActNumber = table.Column<int>(type: "integer", nullable: true),
                    SequenceNumber = table.Column<int>(type: "integer", nullable: true),
                    Slugline = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Synopsis = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieScreenplayScenes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieScreenplayScenes_MovieScenes_MovieSceneId",
                        column: x => x.MovieSceneId,
                        principalTable: "MovieScenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieScreenplayScenes_MovieStoryRevisions_MovieStoryRevisio~",
                        column: x => x.MovieStoryRevisionId,
                        principalTable: "MovieStoryRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieScreenplayElements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieScreenplaySceneId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    ElementType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Content = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    CharacterName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    Parenthetical = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieScreenplayElements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieScreenplayElements_MovieScreenplayScenes_MovieScreenpl~",
                        column: x => x.MovieScreenplaySceneId,
                        principalTable: "MovieScreenplayScenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MovieScreenplayElements_MovieScreenplaySceneId_Ordinal",
                table: "MovieScreenplayElements",
                columns: new[] { "MovieScreenplaySceneId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieScreenplayScenes_MovieSceneId",
                table: "MovieScreenplayScenes",
                column: "MovieSceneId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieScreenplayScenes_MovieStoryRevisionId_Ordinal",
                table: "MovieScreenplayScenes",
                columns: new[] { "MovieStoryRevisionId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieScreenplayScenes_MovieStoryRevisionId_SceneIdentifier",
                table: "MovieScreenplayScenes",
                columns: new[] { "MovieStoryRevisionId", "SceneIdentifier" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieStories_MovieProjectId",
                table: "MovieStories",
                column: "MovieProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieStories_WorkspaceId_UpdatedAt",
                table: "MovieStories",
                columns: new[] { "WorkspaceId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieStoryRevisions_MovieStoryId_RevisionNumber",
                table: "MovieStoryRevisions",
                columns: new[] { "MovieStoryId", "RevisionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieStoryRevisions_MovieStoryId_Status",
                table: "MovieStoryRevisions",
                columns: new[] { "MovieStoryId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MovieScreenplayElements");

            migrationBuilder.DropTable(
                name: "MovieScreenplayScenes");

            migrationBuilder.DropTable(
                name: "MovieStoryRevisions");

            migrationBuilder.DropTable(
                name: "MovieStories");
        }
    }
}
