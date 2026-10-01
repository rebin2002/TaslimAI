using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieReferenceReadinessOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MovieReferenceReadinessOverrideAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieSceneId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieShotId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    BypassedItemKeysJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ReadinessPercentage = table.Column<int>(type: "integer", nullable: false),
                    EvaluationHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieReferenceReadinessOverrideAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieReferenceReadinessOverrideAudits_AspNetUsers_Requested~",
                        column: x => x.RequestedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieReferenceReadinessOverrideAudits_MovieProjects_MoviePr~",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MovieReferenceReadinessOverrideAudits_MovieScenes_MovieScen~",
                        column: x => x.MovieSceneId,
                        principalTable: "MovieScenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MovieReferenceReadinessOverrideAudits_MovieShots_MovieShotId",
                        column: x => x.MovieShotId,
                        principalTable: "MovieShots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MovieReferenceReadinessOverrideAudits_EvaluationHash",
                table: "MovieReferenceReadinessOverrideAudits",
                column: "EvaluationHash");

            migrationBuilder.CreateIndex(
                name: "IX_MovieReferenceReadinessOverrideAudits_MovieProjectId_Create~",
                table: "MovieReferenceReadinessOverrideAudits",
                columns: new[] { "MovieProjectId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieReferenceReadinessOverrideAudits_MovieSceneId",
                table: "MovieReferenceReadinessOverrideAudits",
                column: "MovieSceneId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieReferenceReadinessOverrideAudits_MovieShotId_CreatedAt~",
                table: "MovieReferenceReadinessOverrideAudits",
                columns: new[] { "MovieShotId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieReferenceReadinessOverrideAudits_RequestedByUserId",
                table: "MovieReferenceReadinessOverrideAudits",
                column: "RequestedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MovieReferenceReadinessOverrideAudits");
        }
    }
}
