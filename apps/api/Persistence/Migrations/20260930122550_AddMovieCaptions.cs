using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieCaptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MovieCaptionTracks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieAssemblyId = table.Column<Guid>(type: "uuid", nullable: true),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    TrackType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Language = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: false),
                    IsRtl = table.Column<bool>(type: "boolean", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SourceFormat = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    SourceFileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieCaptionTracks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieCaptionTracks_MovieAssemblies_MovieAssemblyId",
                        column: x => x.MovieAssemblyId,
                        principalTable: "MovieAssemblies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieCaptionTracks_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieCaptionCues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieCaptionTrackId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    StartMilliseconds = table.Column<long>(type: "bigint", nullable: false),
                    EndMilliseconds = table.Column<long>(type: "bigint", nullable: false),
                    Text = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    SpeakerCharacterId = table.Column<Guid>(type: "uuid", nullable: true),
                    SpeakerName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    MovieSceneId = table.Column<Guid>(type: "uuid", nullable: true),
                    MovieShotId = table.Column<Guid>(type: "uuid", nullable: true),
                    MovieTakeId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieCaptionCues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieCaptionCues_MovieCaptionTracks_MovieCaptionTrackId",
                        column: x => x.MovieCaptionTrackId,
                        principalTable: "MovieCaptionTracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MovieCaptionCues_MovieCharacters_SpeakerCharacterId",
                        column: x => x.SpeakerCharacterId,
                        principalTable: "MovieCharacters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieCaptionCues_MovieScenes_MovieSceneId",
                        column: x => x.MovieSceneId,
                        principalTable: "MovieScenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieCaptionCues_MovieShots_MovieShotId",
                        column: x => x.MovieShotId,
                        principalTable: "MovieShots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieCaptionCues_MovieTakes_MovieTakeId",
                        column: x => x.MovieTakeId,
                        principalTable: "MovieTakes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MovieCaptionCues_MovieCaptionTrackId_Sequence",
                table: "MovieCaptionCues",
                columns: new[] { "MovieCaptionTrackId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieCaptionCues_MovieCaptionTrackId_StartMilliseconds_EndM~",
                table: "MovieCaptionCues",
                columns: new[] { "MovieCaptionTrackId", "StartMilliseconds", "EndMilliseconds" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieCaptionCues_MovieSceneId",
                table: "MovieCaptionCues",
                column: "MovieSceneId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieCaptionCues_MovieShotId",
                table: "MovieCaptionCues",
                column: "MovieShotId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieCaptionCues_MovieTakeId",
                table: "MovieCaptionCues",
                column: "MovieTakeId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieCaptionCues_SpeakerCharacterId",
                table: "MovieCaptionCues",
                column: "SpeakerCharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieCaptionTracks_MovieAssemblyId",
                table: "MovieCaptionTracks",
                column: "MovieAssemblyId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieCaptionTracks_MovieProjectId_Language_IsDefault",
                table: "MovieCaptionTracks",
                columns: new[] { "MovieProjectId", "Language", "IsDefault" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieCaptionTracks_MovieProjectId_Sequence",
                table: "MovieCaptionTracks",
                columns: new[] { "MovieProjectId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MovieCaptionCues");

            migrationBuilder.DropTable(
                name: "MovieCaptionTracks");
        }
    }
}
