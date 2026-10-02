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
                name: "AutopilotLocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    AcquiredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LeaseUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutopilotLocks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutopilotWaves",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WaveNumber = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ProductionBaseSha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    IntegrationBranch = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    IntegrationCandidateSha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    DeployedRevision = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ConcurrencyCeiling = table.Column<int>(type: "integer", nullable: false),
                    MaxTaskAttempts = table.Column<int>(type: "integer", nullable: false),
                    MaxGateAttempts = table.Column<int>(type: "integer", nullable: false),
                    DecisionReason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    DecisionNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LaunchIdempotencyKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LaunchFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutopilotWaves", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutopilotAuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WaveId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    RunId = table.Column<Guid>(type: "uuid", nullable: true),
                    EventId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Details = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutopilotAuditEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutopilotAuditEntries_AutopilotWaves_WaveId",
                        column: x => x.WaveId,
                        principalTable: "AutopilotWaves",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AutopilotGates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WaveId = table.Column<Guid>(type: "uuid", nullable: false),
                    GateType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    CandidateSha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Checksum = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EvaluatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutopilotGates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutopilotGates_AutopilotWaves_WaveId",
                        column: x => x.WaveId,
                        principalTable: "AutopilotWaves",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AutopilotTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WaveId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BranchName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    BaseSha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    HeadSha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    Mandatory = table.Column<bool>(type: "boolean", nullable: false),
                    RequiresDecision = table.Column<bool>(type: "boolean", nullable: false),
                    DecisionReason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    LastFailureKind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    LastFailureCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutopilotTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutopilotTasks_AutopilotWaves_WaveId",
                        column: x => x.WaveId,
                        principalTable: "AutopilotWaves",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AutopilotEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    WaveId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    EventType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    BranchName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    BaseSha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    HeadSha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Succeeded = table.Column<bool>(type: "boolean", nullable: true),
                    FailureKind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Processed = table.Column<bool>(type: "boolean", nullable: false),
                    IgnoredReason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutopilotEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutopilotEvents_AutopilotTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "AutopilotTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AutopilotEvents_AutopilotWaves_WaveId",
                        column: x => x.WaveId,
                        principalTable: "AutopilotWaves",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AutopilotRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WaveId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    BranchName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    BaseSha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    HeadSha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LeaseUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutopilotRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutopilotRuns_AutopilotTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "AutopilotTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AutopilotRuns_AutopilotWaves_WaveId",
                        column: x => x.WaveId,
                        principalTable: "AutopilotWaves",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotAuditEntries_EventId",
                table: "AutopilotAuditEntries",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotAuditEntries_WaveId_CreatedAt",
                table: "AutopilotAuditEntries",
                columns: new[] { "WaveId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotEvents_EventId",
                table: "AutopilotEvents",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotEvents_TaskId",
                table: "AutopilotEvents",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotEvents_WaveId_CreatedAt",
                table: "AutopilotEvents",
                columns: new[] { "WaveId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotGates_WaveId_GateType",
                table: "AutopilotGates",
                columns: new[] { "WaveId", "GateType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotLocks_LeaseUntil",
                table: "AutopilotLocks",
                column: "LeaseUntil");

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotLocks_ResourceKey",
                table: "AutopilotLocks",
                column: "ResourceKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotRuns_TaskId_Status",
                table: "AutopilotRuns",
                columns: new[] { "TaskId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotRuns_WaveId_Kind_StartedAt",
                table: "AutopilotRuns",
                columns: new[] { "WaveId", "Kind", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotTasks_WaveId_Status",
                table: "AutopilotTasks",
                columns: new[] { "WaveId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotTasks_WaveId_TaskKey",
                table: "AutopilotTasks",
                columns: new[] { "WaveId", "TaskKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotWaves_LaunchIdempotencyKey",
                table: "AutopilotWaves",
                column: "LaunchIdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotWaves_Status_UpdatedAt",
                table: "AutopilotWaves",
                columns: new[] { "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotWaves_WaveNumber",
                table: "AutopilotWaves",
                column: "WaveNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutopilotAuditEntries");

            migrationBuilder.DropTable(
                name: "AutopilotEvents");

            migrationBuilder.DropTable(
                name: "AutopilotGates");

            migrationBuilder.DropTable(
                name: "AutopilotLocks");

            migrationBuilder.DropTable(
                name: "AutopilotRuns");

            migrationBuilder.DropTable(
                name: "AutopilotTasks");

            migrationBuilder.DropTable(
                name: "AutopilotWaves");
        }
    }
}
