using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUpscalingJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UpscalingAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UpscalingJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ExecutionReference = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Retryable = table.Column<bool>(type: "boolean", nullable: false),
                    SafeMetadataJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpscalingAttempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UpscalingJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    OutputAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetResolution = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    RequestFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceProvenanceJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    OutputProvenanceJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LastErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    MaxRetryCount = table.Column<int>(type: "integer", nullable: false),
                    ProgressPercent = table.Column<int>(type: "integer", nullable: false),
                    CurrentAttemptId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    QueuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    QualityControlAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpscalingJobs", x => x.Id);
                    table.CheckConstraint("CK_UpscalingJobs_ProgressPercent", "\"ProgressPercent\" BETWEEN 0 AND 100");
                    table.ForeignKey(
                        name: "FK_UpscalingJobs_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UpscalingJobs_Assets_OutputAssetId",
                        column: x => x.OutputAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_UpscalingJobs_Assets_SourceAssetId",
                        column: x => x.SourceAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UpscalingJobs_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_UpscalingJobs_UpscalingAttempts_CurrentAttemptId",
                        column: x => x.CurrentAttemptId,
                        principalTable: "UpscalingAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_UpscalingJobs_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UpscalingQualityHandoffs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UpscalingJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    OutputAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetResolution = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ReviewNote = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpscalingQualityHandoffs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UpscalingQualityHandoffs_AspNetUsers_ReviewedByUserId",
                        column: x => x.ReviewedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UpscalingQualityHandoffs_Assets_OutputAssetId",
                        column: x => x.OutputAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UpscalingQualityHandoffs_Assets_SourceAssetId",
                        column: x => x.SourceAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UpscalingQualityHandoffs_UpscalingJobs_UpscalingJobId",
                        column: x => x.UpscalingJobId,
                        principalTable: "UpscalingJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UpscalingAttempts_UpscalingJobId_AttemptNumber",
                table: "UpscalingAttempts",
                columns: new[] { "UpscalingJobId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UpscalingAttempts_UpscalingJobId_IdempotencyKey",
                table: "UpscalingAttempts",
                columns: new[] { "UpscalingJobId", "IdempotencyKey" });

            migrationBuilder.CreateIndex(
                name: "IX_UpscalingJobs_CreatedByUserId_IdempotencyKey",
                table: "UpscalingJobs",
                columns: new[] { "CreatedByUserId", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UpscalingJobs_CurrentAttemptId",
                table: "UpscalingJobs",
                column: "CurrentAttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_UpscalingJobs_OutputAssetId",
                table: "UpscalingJobs",
                column: "OutputAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_UpscalingJobs_ProjectId",
                table: "UpscalingJobs",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_UpscalingJobs_SourceAssetId",
                table: "UpscalingJobs",
                column: "SourceAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_UpscalingJobs_WorkspaceId_CreatedAt",
                table: "UpscalingJobs",
                columns: new[] { "WorkspaceId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UpscalingJobs_WorkspaceId_Status_CreatedAt",
                table: "UpscalingJobs",
                columns: new[] { "WorkspaceId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UpscalingJobs_WorkspaceId_TargetResolution_CreatedAt",
                table: "UpscalingJobs",
                columns: new[] { "WorkspaceId", "TargetResolution", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UpscalingQualityHandoffs_OutputAssetId_Status",
                table: "UpscalingQualityHandoffs",
                columns: new[] { "OutputAssetId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_UpscalingQualityHandoffs_ReviewedByUserId",
                table: "UpscalingQualityHandoffs",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_UpscalingQualityHandoffs_SourceAssetId",
                table: "UpscalingQualityHandoffs",
                column: "SourceAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_UpscalingQualityHandoffs_UpscalingJobId_CreatedAt",
                table: "UpscalingQualityHandoffs",
                columns: new[] { "UpscalingJobId", "CreatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_UpscalingAttempts_UpscalingJobs_UpscalingJobId",
                table: "UpscalingAttempts",
                column: "UpscalingJobId",
                principalTable: "UpscalingJobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_UpscalingAttempts_UpscalingJobs_UpscalingJobId",
                table: "UpscalingAttempts");

            migrationBuilder.DropTable(
                name: "UpscalingQualityHandoffs");

            migrationBuilder.DropTable(
                name: "UpscalingJobs");

            migrationBuilder.DropTable(
                name: "UpscalingAttempts");
        }
    }
}
