using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieCanonicalTimeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MovieTimelineItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieTimelineTrackId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SourceTakeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    TimelineInMilliseconds = table.Column<int>(type: "integer", nullable: false),
                    TimelineOutMilliseconds = table.Column<int>(type: "integer", nullable: false),
                    SourceInMilliseconds = table.Column<int>(type: "integer", nullable: true),
                    SourceOutMilliseconds = table.Column<int>(type: "integer", nullable: true),
                    DurationMilliseconds = table.Column<int>(type: "integer", nullable: false),
                    Label = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    MetadataJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieTimelineItems", x => x.Id);
                    table.CheckConstraint("CK_MovieTimelineItems_Duration", "\"DurationMilliseconds\" = \"TimelineOutMilliseconds\" - \"TimelineInMilliseconds\"");
                    table.CheckConstraint("CK_MovieTimelineItems_TimelineRange", "\"TimelineInMilliseconds\" >= 0 AND \"TimelineOutMilliseconds\" > \"TimelineInMilliseconds\"");
                    table.ForeignKey(
                        name: "FK_MovieTimelineItems_Assets_SourceAssetId",
                        column: x => x.SourceAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieTimelineItems_MovieTakes_SourceTakeId",
                        column: x => x.SourceTakeId,
                        principalTable: "MovieTakes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MovieTimelineRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieTimelineId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionNumber = table.Column<int>(type: "integer", nullable: false),
                    BaseRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Label = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    ChangeSummary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DurationMilliseconds = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LockedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LockedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieTimelineRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieTimelineRevisions_MovieTimelineRevisions_BaseRevisionId",
                        column: x => x.BaseRevisionId,
                        principalTable: "MovieTimelineRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MovieTimelines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    CurrentRevisionNumber = table.Column<int>(type: "integer", nullable: false),
                    CurrentRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    LockedRevisionNumber = table.Column<int>(type: "integer", nullable: true),
                    LockedRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieTimelines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieTimelines_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MovieTimelines_MovieTimelineRevisions_CurrentRevisionId",
                        column: x => x.CurrentRevisionId,
                        principalTable: "MovieTimelineRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieTimelines_MovieTimelineRevisions_LockedRevisionId",
                        column: x => x.LockedRevisionId,
                        principalTable: "MovieTimelineRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MovieTimelineTracks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieTimelineRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackNumber = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    IsMuted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieTimelineTracks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieTimelineTracks_MovieTimelineRevisions_MovieTimelineRev~",
                        column: x => x.MovieTimelineRevisionId,
                        principalTable: "MovieTimelineRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MovieTimelineItems_MovieTimelineTrackId_Sequence",
                table: "MovieTimelineItems",
                columns: new[] { "MovieTimelineTrackId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieTimelineItems_SourceAssetId",
                table: "MovieTimelineItems",
                column: "SourceAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieTimelineItems_SourceTakeId",
                table: "MovieTimelineItems",
                column: "SourceTakeId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieTimelineRevisions_BaseRevisionId",
                table: "MovieTimelineRevisions",
                column: "BaseRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieTimelineRevisions_MovieTimelineId_RevisionNumber",
                table: "MovieTimelineRevisions",
                columns: new[] { "MovieTimelineId", "RevisionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieTimelineRevisions_MovieTimelineId_Status",
                table: "MovieTimelineRevisions",
                columns: new[] { "MovieTimelineId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieTimelines_CurrentRevisionId",
                table: "MovieTimelines",
                column: "CurrentRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieTimelines_LockedRevisionId",
                table: "MovieTimelines",
                column: "LockedRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieTimelines_MovieProjectId",
                table: "MovieTimelines",
                column: "MovieProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieTimelineTracks_MovieTimelineRevisionId_TrackNumber",
                table: "MovieTimelineTracks",
                columns: new[] { "MovieTimelineRevisionId", "TrackNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_MovieTimelineItems_MovieTimelineTracks_MovieTimelineTrackId",
                table: "MovieTimelineItems",
                column: "MovieTimelineTrackId",
                principalTable: "MovieTimelineTracks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_MovieTimelineRevisions_MovieTimelines_MovieTimelineId",
                table: "MovieTimelineRevisions",
                column: "MovieTimelineId",
                principalTable: "MovieTimelines",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MovieTimelineRevisions_MovieTimelines_MovieTimelineId",
                table: "MovieTimelineRevisions");

            migrationBuilder.DropTable(
                name: "MovieTimelineItems");

            migrationBuilder.DropTable(
                name: "MovieTimelineTracks");

            migrationBuilder.DropTable(
                name: "MovieTimelines");

            migrationBuilder.DropTable(
                name: "MovieTimelineRevisions");
        }
    }
}
