using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminOperationsObservability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminOperationAuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    TargetType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RequestId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    BeforeState = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    AfterState = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminOperationAuditEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdminOperationAuditEvents_AspNetUsers_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GenerationWorkerHeartbeats",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkerId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    InstanceId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    WorkerIndex = table.Column<int>(type: "integer", nullable: false),
                    WorkerConcurrency = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastClaimedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastCompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ActiveJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConsecutiveIterationFailures = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenerationWorkerHeartbeats", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminOperationAuditEvents_ActorUserId",
                table: "AdminOperationAuditEvents",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminOperationAuditEvents_CreatedAt_Action",
                table: "AdminOperationAuditEvents",
                columns: new[] { "CreatedAt", "Action" });

            migrationBuilder.CreateIndex(
                name: "IX_AdminOperationAuditEvents_TargetType_TargetId_CreatedAt",
                table: "AdminOperationAuditEvents",
                columns: new[] { "TargetType", "TargetId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GenerationWorkerHeartbeats_LastSeenAt",
                table: "GenerationWorkerHeartbeats",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_GenerationWorkerHeartbeats_WorkerId",
                table: "GenerationWorkerHeartbeats",
                column: "WorkerId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminOperationAuditEvents");

            migrationBuilder.DropTable(
                name: "GenerationWorkerHeartbeats");
        }
    }
}
