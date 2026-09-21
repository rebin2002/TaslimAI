using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGenerationJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GenerationJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    Provider = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ProviderModel = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    InputJson = table.Column<string>(type: "character varying(100000)", maxLength: 100000, nullable: false),
                    ResultJson = table.Column<string>(type: "character varying(100000)", maxLength: 100000, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ProgressPercent = table.Column<int>(type: "integer", nullable: false),
                    CancellationRequested = table.Column<bool>(type: "boolean", nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    QueuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClaimExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenerationJobs", x => x.Id);
                    table.CheckConstraint("CK_GenerationJobs_ProgressPercent", "\"ProgressPercent\" BETWEEN 0 AND 100");
                    table.ForeignKey(
                        name: "FK_GenerationJobs_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GenerationJobs_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_GenerationJobs_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GenerationJobOutputs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    StoredFileId = table.Column<Guid>(type: "uuid", nullable: true),
                    OutputType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    MetadataJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenerationJobOutputs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GenerationJobOutputs_GenerationJobs_GenerationJobId",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GenerationJobOutputs_StoredFiles_StoredFileId",
                        column: x => x.StoredFileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GenerationJobOutputs_GenerationJobId",
                table: "GenerationJobOutputs",
                column: "GenerationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_GenerationJobOutputs_StoredFileId",
                table: "GenerationJobOutputs",
                column: "StoredFileId");

            migrationBuilder.CreateIndex(
                name: "IX_GenerationJobs_CreatedByUserId",
                table: "GenerationJobs",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_GenerationJobs_ProjectId",
                table: "GenerationJobs",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_GenerationJobs_Status_QueuedAt_CreatedAt",
                table: "GenerationJobs",
                columns: new[] { "Status", "QueuedAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GenerationJobs_WorkspaceId_CreatedAt",
                table: "GenerationJobs",
                columns: new[] { "WorkspaceId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GenerationJobs_WorkspaceId_Status_CreatedAt",
                table: "GenerationJobs",
                columns: new[] { "WorkspaceId", "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GenerationJobOutputs");

            migrationBuilder.DropTable(
                name: "GenerationJobs");
        }
    }
}
