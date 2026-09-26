using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieStudioCollaboration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MovieComments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ParentCommentId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieComments_AspNetUsers_AuthorUserId",
                        column: x => x.AuthorUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieComments_MovieComments_ParentCommentId",
                        column: x => x.ParentCommentId,
                        principalTable: "MovieComments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieComments_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieProductionAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssigneeUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    DueAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieProductionAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieProductionAssignments_AspNetUsers_AssignedByUserId",
                        column: x => x.AssignedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieProductionAssignments_AspNetUsers_AssigneeUserId",
                        column: x => x.AssigneeUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieProductionAssignments_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieProductionCredits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CreditName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieProductionCredits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieProductionCredits_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieProductionCredits_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsFinal = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RequestNote = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    DecisionNote = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieReviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieReviews_AspNetUsers_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieReviews_AspNetUsers_ReviewerUserId",
                        column: x => x.ReviewerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieReviews_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieTeamMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    IsProjectOwner = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieTeamMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieTeamMembers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieTeamMembers_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieCommentMentions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CommentId = table.Column<Guid>(type: "uuid", nullable: false),
                    MentionedUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieCommentMentions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieCommentMentions_AspNetUsers_MentionedUserId",
                        column: x => x.MentionedUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieCommentMentions_MovieComments_CommentId",
                        column: x => x.CommentId,
                        principalTable: "MovieComments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieTeamMemberPermissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieTeamMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    Permission = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Granted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieTeamMemberPermissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieTeamMemberPermissions_MovieTeamMembers_MovieTeamMember~",
                        column: x => x.MovieTeamMemberId,
                        principalTable: "MovieTeamMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                INSERT INTO "MovieTeamMembers" ("Id", "MovieProjectId", "UserId", "Role", "IsProjectOwner", "CreatedAt", "UpdatedAt")
                SELECT project."Id", project."Id", project."CreatedByUserId", 'Producer', TRUE, project."CreatedAt", project."UpdatedAt"
                FROM "MovieProjects" project
                WHERE NOT EXISTS (
                    SELECT 1 FROM "MovieTeamMembers" member
                    WHERE member."MovieProjectId" = project."Id" AND member."UserId" = project."CreatedByUserId"
                );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_MovieCommentMentions_CommentId_MentionedUserId",
                table: "MovieCommentMentions",
                columns: new[] { "CommentId", "MentionedUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieCommentMentions_MentionedUserId",
                table: "MovieCommentMentions",
                column: "MentionedUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieComments_AuthorUserId",
                table: "MovieComments",
                column: "AuthorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieComments_MovieProjectId_TargetType_TargetId_CreatedAt",
                table: "MovieComments",
                columns: new[] { "MovieProjectId", "TargetType", "TargetId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieComments_ParentCommentId",
                table: "MovieComments",
                column: "ParentCommentId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionAssignments_AssignedByUserId",
                table: "MovieProductionAssignments",
                column: "AssignedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionAssignments_AssigneeUserId",
                table: "MovieProductionAssignments",
                column: "AssigneeUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionAssignments_MovieProjectId_Status_DueAt",
                table: "MovieProductionAssignments",
                columns: new[] { "MovieProjectId", "Status", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionCredits_MovieProjectId_SortOrder",
                table: "MovieProductionCredits",
                columns: new[] { "MovieProjectId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionCredits_MovieProjectId_UserId_Role",
                table: "MovieProductionCredits",
                columns: new[] { "MovieProjectId", "UserId", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionCredits_UserId",
                table: "MovieProductionCredits",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieReviews_MovieProjectId_TargetType_TargetId_CreatedAt",
                table: "MovieReviews",
                columns: new[] { "MovieProjectId", "TargetType", "TargetId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieReviews_RequestedByUserId",
                table: "MovieReviews",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieReviews_ReviewerUserId_Status",
                table: "MovieReviews",
                columns: new[] { "ReviewerUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieTeamMemberPermissions_MovieTeamMemberId_Permission",
                table: "MovieTeamMemberPermissions",
                columns: new[] { "MovieTeamMemberId", "Permission" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieTeamMembers_MovieProjectId_UserId",
                table: "MovieTeamMembers",
                columns: new[] { "MovieProjectId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieTeamMembers_UserId",
                table: "MovieTeamMembers",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MovieCommentMentions");

            migrationBuilder.DropTable(
                name: "MovieProductionAssignments");

            migrationBuilder.DropTable(
                name: "MovieProductionCredits");

            migrationBuilder.DropTable(
                name: "MovieReviews");

            migrationBuilder.DropTable(
                name: "MovieTeamMemberPermissions");

            migrationBuilder.DropTable(
                name: "MovieComments");

            migrationBuilder.DropTable(
                name: "MovieTeamMembers");
        }
    }
}
