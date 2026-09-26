using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieDirectorFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DirectorProjectContexts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContextVersion = table.Column<int>(type: "integer", nullable: false),
                    SnapshotJson = table.Column<string>(type: "character varying(100000)", maxLength: 100000, nullable: false),
                    SnapshotHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectorProjectContexts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DirectorProjectContexts_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DirectorProjectContexts_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DirectorDecisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DirectorProjectContextId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieShotId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecisionType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    QualityLevel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RationaleJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    EstimatedCostUsd = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectorDecisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DirectorDecisions_DirectorProjectContexts_DirectorProjectCo~",
                        column: x => x.DirectorProjectContextId,
                        principalTable: "DirectorProjectContexts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DirectorDecisions_MovieShots_MovieShotId",
                        column: x => x.MovieShotId,
                        principalTable: "MovieShots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "DirectorProposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    DirectorProjectContextId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Summary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    RationaleJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RejectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectorProposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DirectorProposals_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DirectorProposals_DirectorProjectContexts_DirectorProjectCo~",
                        column: x => x.DirectorProjectContextId,
                        principalTable: "DirectorProjectContexts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DirectorProposals_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DirectorProposals_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DirectorActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    DirectorProposalId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActionType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ApprovalRequired = table.Column<bool>(type: "boolean", nullable: false),
                    PayloadJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectorActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DirectorActions_DirectorProposals_DirectorProposalId",
                        column: x => x.DirectorProposalId,
                        principalTable: "DirectorProposals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DirectorActions_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DirectorActions_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DirectorActionResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DirectorActionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ResultJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    SafeMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectorActionResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DirectorActionResults_DirectorActions_DirectorActionId",
                        column: x => x.DirectorActionId,
                        principalTable: "DirectorActions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DirectorHistoryEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    DirectorProposalId = table.Column<Guid>(type: "uuid", nullable: true),
                    DirectorActionId = table.Column<Guid>(type: "uuid", nullable: true),
                    EventType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SafeDetailsJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectorHistoryEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DirectorHistoryEvents_DirectorActions_DirectorActionId",
                        column: x => x.DirectorActionId,
                        principalTable: "DirectorActions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DirectorHistoryEvents_DirectorProposals_DirectorProposalId",
                        column: x => x.DirectorProposalId,
                        principalTable: "DirectorProposals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DirectorHistoryEvents_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DirectorActionResults_DirectorActionId_CreatedAt",
                table: "DirectorActionResults",
                columns: new[] { "DirectorActionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectorActions_DirectorProposalId_CreatedAt",
                table: "DirectorActions",
                columns: new[] { "DirectorProposalId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectorActions_MovieProjectId",
                table: "DirectorActions",
                column: "MovieProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_DirectorActions_WorkspaceId_Status_CreatedAt",
                table: "DirectorActions",
                columns: new[] { "WorkspaceId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectorDecisions_DirectorProjectContextId_CreatedAt",
                table: "DirectorDecisions",
                columns: new[] { "DirectorProjectContextId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectorDecisions_MovieShotId",
                table: "DirectorDecisions",
                column: "MovieShotId");

            migrationBuilder.CreateIndex(
                name: "IX_DirectorHistoryEvents_DirectorActionId",
                table: "DirectorHistoryEvents",
                column: "DirectorActionId");

            migrationBuilder.CreateIndex(
                name: "IX_DirectorHistoryEvents_DirectorProposalId",
                table: "DirectorHistoryEvents",
                column: "DirectorProposalId");

            migrationBuilder.CreateIndex(
                name: "IX_DirectorHistoryEvents_WorkspaceId_CreatedAt",
                table: "DirectorHistoryEvents",
                columns: new[] { "WorkspaceId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectorProjectContexts_MovieProjectId",
                table: "DirectorProjectContexts",
                column: "MovieProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DirectorProjectContexts_WorkspaceId_UpdatedAt",
                table: "DirectorProjectContexts",
                columns: new[] { "WorkspaceId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectorProposals_CreatedByUserId",
                table: "DirectorProposals",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DirectorProposals_DirectorProjectContextId",
                table: "DirectorProposals",
                column: "DirectorProjectContextId");

            migrationBuilder.CreateIndex(
                name: "IX_DirectorProposals_MovieProjectId_CreatedAt",
                table: "DirectorProposals",
                columns: new[] { "MovieProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectorProposals_WorkspaceId_Status_CreatedAt",
                table: "DirectorProposals",
                columns: new[] { "WorkspaceId", "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DirectorActionResults");

            migrationBuilder.DropTable(
                name: "DirectorDecisions");

            migrationBuilder.DropTable(
                name: "DirectorHistoryEvents");

            migrationBuilder.DropTable(
                name: "DirectorActions");

            migrationBuilder.DropTable(
                name: "DirectorProposals");

            migrationBuilder.DropTable(
                name: "DirectorProjectContexts");
        }
    }
}
