using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAutopilotControllerFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AutopilotAuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    TargetType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Reason = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    StatusDetail = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: true),
                    WaveKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    TaskId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RunState = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    TaskState = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    Branch = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    BaseSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CandidateSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RequestId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DryRun = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutopilotAuditEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutopilotAuditEvents_AspNetUsers_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AutopilotControlStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ControlKey = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Paused = table.Column<bool>(type: "boolean", nullable: false),
                    KillSwitchEngaged = table.Column<bool>(type: "boolean", nullable: false),
                    LastReason = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    LastActor = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutopilotControlStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutopilotEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SourceSystem = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ExternalEventId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    PayloadHash = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SignatureVerified = table.Column<bool>(type: "boolean", nullable: false),
                    ReplayProtected = table.Column<bool>(type: "boolean", nullable: false),
                    Reason = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    WaveKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    TaskId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Branch = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    BaseSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CandidateSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    ReconciliationCount = table.Column<int>(type: "integer", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: true),
                    WaveTaskRecordId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    QueuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ProcessingStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false),
                    SignalJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutopilotEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutopilotLocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceKey = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    OwnerToken = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    FencingToken = table.Column<long>(type: "bigint", nullable: false),
                    AcquiredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HeldBy = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutopilotLocks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutopilotRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WaveKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    RunKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    State = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    BaseSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CandidateSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Branch = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    TaskCount = table.Column<int>(type: "integer", nullable: false),
                    SucceededTaskCount = table.Column<int>(type: "integer", nullable: false),
                    FailedTaskCount = table.Column<int>(type: "integer", nullable: false),
                    RetryScheduledCount = table.Column<int>(type: "integer", nullable: false),
                    ReconciliationCount = table.Column<int>(type: "integer", nullable: false),
                    ExpectedTaskCount = table.Column<int>(type: "integer", nullable: false),
                    WaveClosed = table.Column<bool>(type: "boolean", nullable: false),
                    WaveCompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IntegrationGatePassed = table.Column<bool>(type: "boolean", nullable: false),
                    ReleaseGatePassed = table.Column<bool>(type: "boolean", nullable: false),
                    HumanDecisionRequired = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    LastReason = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    LastStatusDetail = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastEventAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutopilotRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutopilotGateEvaluations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    WaveKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    GateKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CandidateSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BaseSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FailedChecksJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutopilotGateEvaluations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutopilotGateEvaluations_AutopilotRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "AutopilotRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AutopilotReleaseHandoffs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    WaveKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CandidateSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BaseSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Branch = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    DryRun = table.Column<bool>(type: "boolean", nullable: false),
                    SmokeStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    SmokeReason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LastStatusDetail = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutopilotReleaseHandoffs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutopilotReleaseHandoffs_AutopilotRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "AutopilotRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AutopilotWaveTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    WaveKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    TaskId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    State = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    FailureClass = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    RequiresHumanDecision = table.Column<bool>(type: "boolean", nullable: false),
                    HumanDecisionKind = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    Branch = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    BaseSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CandidateSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    EvidenceSummary = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: true),
                    ChecksJson = table.Column<string>(type: "text", nullable: true),
                    LastReason = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastEventAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutopilotWaveTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutopilotWaveTasks_AutopilotRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "AutopilotRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotAuditEvents_ActorUserId",
                table: "AutopilotAuditEvents",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotAuditEvents_CreatedAt_Action",
                table: "AutopilotAuditEvents",
                columns: new[] { "CreatedAt", "Action" });

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotAuditEvents_WaveKey_CreatedAt",
                table: "AutopilotAuditEvents",
                columns: new[] { "WaveKey", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotControlStates_ControlKey",
                table: "AutopilotControlStates",
                column: "ControlKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotEvents_IdempotencyKey",
                table: "AutopilotEvents",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotEvents_SourceSystem_ExternalEventId",
                table: "AutopilotEvents",
                columns: new[] { "SourceSystem", "ExternalEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotEvents_Status_NextAttemptAt",
                table: "AutopilotEvents",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotEvents_Status_ReceivedAt",
                table: "AutopilotEvents",
                columns: new[] { "Status", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotGateEvaluations_RunId_GateKind_CandidateSha",
                table: "AutopilotGateEvaluations",
                columns: new[] { "RunId", "GateKind", "CandidateSha" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotLocks_ExpiresAt",
                table: "AutopilotLocks",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotLocks_ResourceKey",
                table: "AutopilotLocks",
                column: "ResourceKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotReleaseHandoffs_RunId",
                table: "AutopilotReleaseHandoffs",
                column: "RunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotRuns_State_UpdatedAt",
                table: "AutopilotRuns",
                columns: new[] { "State", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotRuns_WaveKey",
                table: "AutopilotRuns",
                column: "WaveKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotWaveTasks_RunId_TaskId",
                table: "AutopilotWaveTasks",
                columns: new[] { "RunId", "TaskId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotWaveTasks_State_UpdatedAt",
                table: "AutopilotWaveTasks",
                columns: new[] { "State", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutopilotAuditEvents");

            migrationBuilder.DropTable(
                name: "AutopilotControlStates");

            migrationBuilder.DropTable(
                name: "AutopilotEvents");

            migrationBuilder.DropTable(
                name: "AutopilotGateEvaluations");

            migrationBuilder.DropTable(
                name: "AutopilotLocks");

            migrationBuilder.DropTable(
                name: "AutopilotReleaseHandoffs");

            migrationBuilder.DropTable(
                name: "AutopilotWaveTasks");

            migrationBuilder.DropTable(
                name: "AutopilotRuns");
        }
    }
}
