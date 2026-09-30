using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieProductionCheckpoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MovieProductionCheckpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ProgressPercent = table.Column<int>(type: "integer", nullable: false),
                    TotalShots = table.Column<int>(type: "integer", nullable: false),
                    CompletedShots = table.Column<int>(type: "integer", nullable: false),
                    RunningShots = table.Column<int>(type: "integer", nullable: false),
                    BlockedShots = table.Column<int>(type: "integer", nullable: false),
                    RecoverableShots = table.Column<int>(type: "integer", nullable: false),
                    PendingApprovalShots = table.Column<int>(type: "integer", nullable: false),
                    SnapshotHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ObservedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastRecoveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastRecoveredByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieProductionCheckpoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieProductionCheckpoints_AspNetUsers_LastRecoveredByUserId",
                        column: x => x.LastRecoveredByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieProductionCheckpoints_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionCheckpoints_LastRecoveredByUserId",
                table: "MovieProductionCheckpoints",
                column: "LastRecoveredByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionCheckpoints_MovieProjectId",
                table: "MovieProductionCheckpoints",
                column: "MovieProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionCheckpoints_MovieProjectId_ObservedAt",
                table: "MovieProductionCheckpoints",
                columns: new[] { "MovieProjectId", "ObservedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MovieProductionCheckpoints");
        }
    }
}
