using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAutopilotNextWaveLaunchAndBacklog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AutopilotBacklogItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    AcceptanceSummary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Approved = table.Column<bool>(type: "boolean", nullable: false),
                    ApprovedBy = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovalNote = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    ConsumedByWaveKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    ConsumedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutopilotBacklogItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutopilotWaveLaunchBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceWaveKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    WaveKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    LastStatusDetail = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: true),
                    HumanDecisionRequired = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    BaseSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Branch = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    PlanFingerprint = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    TaskCount = table.Column<int>(type: "integer", nullable: false),
                    LaunchedTaskCount = table.Column<int>(type: "integer", nullable: false),
                    DryRun = table.Column<bool>(type: "boolean", nullable: false),
                    LaunchAttempt = table.Column<int>(type: "integer", nullable: false),
                    MaxLaunchAttempts = table.Column<int>(type: "integer", nullable: false),
                    ReconcileCount = table.Column<int>(type: "integer", nullable: false),
                    MaxReconciles = table.Column<int>(type: "integer", nullable: false),
                    NextReconcileAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LaunchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutopilotWaveLaunchBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutopilotWaveLaunchBatches_AutopilotRuns_SourceRunId",
                        column: x => x.SourceRunId,
                        principalTable: "AutopilotRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AutopilotWaveLaunchTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceWaveKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    WaveKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    TaskKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    BacklogItemKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ExternalTaskId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ExternalRef = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    LastStatusDetail = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: true),
                    Branch = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    BaseSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DryRun = table.Column<bool>(type: "boolean", nullable: false),
                    LaunchAttempt = table.Column<int>(type: "integer", nullable: false),
                    MaxLaunchAttempts = table.Column<int>(type: "integer", nullable: false),
                    ReconcileCount = table.Column<int>(type: "integer", nullable: false),
                    MaxReconciles = table.Column<int>(type: "integer", nullable: false),
                    NextReconcileAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LaunchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutopilotWaveLaunchTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutopilotWaveLaunchTasks_AutopilotWaveLaunchBatches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "AutopilotWaveLaunchBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotBacklogItems_Approved_Kind_ConsumedByWaveKey",
                table: "AutopilotBacklogItems",
                columns: new[] { "Approved", "Kind", "ConsumedByWaveKey" });

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotBacklogItems_ItemKey",
                table: "AutopilotBacklogItems",
                column: "ItemKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotWaveLaunchBatches_SourceRunId_WaveKey",
                table: "AutopilotWaveLaunchBatches",
                columns: new[] { "SourceRunId", "WaveKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotWaveLaunchBatches_Status_UpdatedAt",
                table: "AutopilotWaveLaunchBatches",
                columns: new[] { "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotWaveLaunchTasks_BatchId_TaskKey",
                table: "AutopilotWaveLaunchTasks",
                columns: new[] { "BatchId", "TaskKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotWaveLaunchTasks_ExternalTaskId",
                table: "AutopilotWaveLaunchTasks",
                column: "ExternalTaskId",
                unique: true,
                filter: "\"ExternalTaskId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotWaveLaunchTasks_Status_NextReconcileAt",
                table: "AutopilotWaveLaunchTasks",
                columns: new[] { "Status", "NextReconcileAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutopilotBacklogItems");

            migrationBuilder.DropTable(
                name: "AutopilotWaveLaunchTasks");

            migrationBuilder.DropTable(
                name: "AutopilotWaveLaunchBatches");
        }
    }
}
