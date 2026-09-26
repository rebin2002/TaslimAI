using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieCharacterContinuityEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContinuitySnapshotHash",
                table: "MovieProductionVersions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ContinuitySnapshotId",
                table: "MovieProductionVersions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ContinuitySnapshotVersion",
                table: "MovieProductionVersions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContinuitySnapshotHash",
                table: "MovieClips",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ContinuitySnapshotId",
                table: "MovieClips",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ContinuitySnapshotVersion",
                table: "MovieClips",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MovieCharacterContinuitySnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieSceneId = table.Column<Guid>(type: "uuid", nullable: true),
                    MovieShotId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    SnapshotJson = table.Column<string>(type: "character varying(60000)", maxLength: 60000, nullable: false),
                    SnapshotHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieCharacterContinuitySnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieCharacterContinuitySnapshots_MovieProjects_MovieProjec~",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MovieCharacterContinuitySnapshots_MovieScenes_MovieSceneId",
                        column: x => x.MovieSceneId,
                        principalTable: "MovieScenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MovieCharacterContinuitySnapshots_MovieShots_MovieShotId",
                        column: x => x.MovieShotId,
                        principalTable: "MovieShots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MovieCharacterContinuitySnapshots_MovieProjectId_CreatedAt",
                table: "MovieCharacterContinuitySnapshots",
                columns: new[] { "MovieProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieCharacterContinuitySnapshots_MovieProjectId_MovieScene~",
                table: "MovieCharacterContinuitySnapshots",
                columns: new[] { "MovieProjectId", "MovieSceneId", "MovieShotId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieCharacterContinuitySnapshots_MovieSceneId",
                table: "MovieCharacterContinuitySnapshots",
                column: "MovieSceneId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieCharacterContinuitySnapshots_MovieShotId",
                table: "MovieCharacterContinuitySnapshots",
                column: "MovieShotId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MovieCharacterContinuitySnapshots");

            migrationBuilder.DropColumn(
                name: "ContinuitySnapshotHash",
                table: "MovieProductionVersions");

            migrationBuilder.DropColumn(
                name: "ContinuitySnapshotId",
                table: "MovieProductionVersions");

            migrationBuilder.DropColumn(
                name: "ContinuitySnapshotVersion",
                table: "MovieProductionVersions");

            migrationBuilder.DropColumn(
                name: "ContinuitySnapshotHash",
                table: "MovieClips");

            migrationBuilder.DropColumn(
                name: "ContinuitySnapshotId",
                table: "MovieClips");

            migrationBuilder.DropColumn(
                name: "ContinuitySnapshotVersion",
                table: "MovieClips");
        }
    }
}
