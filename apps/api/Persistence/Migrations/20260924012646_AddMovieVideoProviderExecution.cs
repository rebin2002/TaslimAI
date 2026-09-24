using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieVideoProviderExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContinuitySnapshotJson",
                table: "MovieClips",
                type: "character varying(20000)",
                maxLength: 20000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MovieSceneId",
                table: "MovieClips",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MovieVideoProviderExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieClipId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ProviderJobId = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    PollCount = table.Column<int>(type: "integer", nullable: false),
                    ProgressPercent = table.Column<int>(type: "integer", nullable: false),
                    LastErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    NextPollAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieVideoProviderExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieVideoProviderExecutions_GenerationJobs_GenerationJobId",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MovieVideoProviderExecutions_MovieClips_MovieClipId",
                        column: x => x.MovieClipId,
                        principalTable: "MovieClips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MovieClips_MovieSceneId",
                table: "MovieClips",
                column: "MovieSceneId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieVideoProviderExecutions_GenerationJobId",
                table: "MovieVideoProviderExecutions",
                column: "GenerationJobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieVideoProviderExecutions_MovieClipId",
                table: "MovieVideoProviderExecutions",
                column: "MovieClipId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieVideoProviderExecutions_Status_NextPollAt",
                table: "MovieVideoProviderExecutions",
                columns: new[] { "Status", "NextPollAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_MovieClips_MovieScenes_MovieSceneId",
                table: "MovieClips",
                column: "MovieSceneId",
                principalTable: "MovieScenes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MovieClips_MovieScenes_MovieSceneId",
                table: "MovieClips");

            migrationBuilder.DropTable(
                name: "MovieVideoProviderExecutions");

            migrationBuilder.DropIndex(
                name: "IX_MovieClips_MovieSceneId",
                table: "MovieClips");

            migrationBuilder.DropColumn(
                name: "ContinuitySnapshotJson",
                table: "MovieClips");

            migrationBuilder.DropColumn(
                name: "MovieSceneId",
                table: "MovieClips");
        }
    }
}
