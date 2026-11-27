using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieMissingInsertDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MovieMissingInsertDecisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProposalId = table.Column<Guid>(type: "uuid", nullable: false),
                    GapId = table.Column<Guid>(type: "uuid", nullable: false),
                    TimelineRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TimelineRevisionNumber = table.Column<int>(type: "integer", nullable: false),
                    TrackId = table.Column<Guid>(type: "uuid", nullable: false),
                    BeforeTimelineItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    AfterTimelineItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    TimelineInMilliseconds = table.Column<int>(type: "integer", nullable: false),
                    TimelineOutMilliseconds = table.Column<int>(type: "integer", nullable: false),
                    BeforeShotId = table.Column<Guid>(type: "uuid", nullable: true),
                    AfterShotId = table.Column<Guid>(type: "uuid", nullable: true),
                    SceneId = table.Column<Guid>(type: "uuid", nullable: true),
                    StorySceneId = table.Column<Guid>(type: "uuid", nullable: true),
                    AnchorShotId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedStoryRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    LockedGuideRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProductionKitHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ProductionKitSchemaVersion = table.Column<int>(type: "integer", nullable: true),
                    AnchorTakeId = table.Column<Guid>(type: "uuid", nullable: true),
                    AnchorSelectId = table.Column<Guid>(type: "uuid", nullable: true),
                    SelectedTakeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SelectedSelectId = table.Column<Guid>(type: "uuid", nullable: true),
                    ContractVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    InsertType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    DurationMilliseconds = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ProposalGroundingJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ContinuityAnchorJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    ScreenDirectionAnchorJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewNote = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    AppliedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AppliedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AppliedTimelineRevisionId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieMissingInsertDecisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieMissingInsertDecisions_AspNetUsers_AppliedByUserId",
                        column: x => x.AppliedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieMissingInsertDecisions_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieMissingInsertDecisions_AspNetUsers_ReviewedByUserId",
                        column: x => x.ReviewedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieMissingInsertDecisions_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(name: "IX_MovieMissingInsertDecisions_AnchorSelectId", table: "MovieMissingInsertDecisions", column: "AnchorSelectId");
            migrationBuilder.CreateIndex(name: "IX_MovieMissingInsertDecisions_AnchorTakeId", table: "MovieMissingInsertDecisions", column: "AnchorTakeId");
            migrationBuilder.CreateIndex(name: "IX_MovieMissingInsertDecisions_AppliedByUserId", table: "MovieMissingInsertDecisions", column: "AppliedByUserId");
            migrationBuilder.CreateIndex(name: "IX_MovieMissingInsertDecisions_CreatedByUserId", table: "MovieMissingInsertDecisions", column: "CreatedByUserId");
            migrationBuilder.CreateIndex(name: "IX_MovieMissingInsertDecisions_MovieProjectId_Status_CreatedAt", table: "MovieMissingInsertDecisions", columns: new[] { "MovieProjectId", "Status", "CreatedAt" });
            migrationBuilder.CreateIndex(name: "IX_MovieMissingInsertDecisions_MovieProjectId_TimelineRevisionId_ProposalId", table: "MovieMissingInsertDecisions", columns: new[] { "MovieProjectId", "TimelineRevisionId", "ProposalId" }, unique: true);
            migrationBuilder.CreateIndex(name: "IX_MovieMissingInsertDecisions_ReviewedByUserId", table: "MovieMissingInsertDecisions", column: "ReviewedByUserId");
            migrationBuilder.CreateIndex(name: "IX_MovieMissingInsertDecisions_TimelineRevisionId", table: "MovieMissingInsertDecisions", column: "TimelineRevisionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "MovieMissingInsertDecisions");
        }
    }
}
