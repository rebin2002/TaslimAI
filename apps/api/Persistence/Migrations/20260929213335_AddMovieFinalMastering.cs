using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieFinalMastering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MovieFinalMasters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieShotId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceTakeId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceMovieClipId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    OutputAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    GenerationJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetProfile = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SourceWidth = table.Column<int>(type: "integer", nullable: true),
                    SourceHeight = table.Column<int>(type: "integer", nullable: true),
                    TargetWidth = table.Column<int>(type: "integer", nullable: false),
                    TargetHeight = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    StateReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    QcStatus = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    QcResultJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    ProvenanceJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    SupersedesMasterId = table.Column<Guid>(type: "uuid", nullable: true),
                    SupersededByMasterId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SupersededAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieFinalMasters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieFinalMasters_AspNetUsers_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieFinalMasters_Assets_OutputAssetId",
                        column: x => x.OutputAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieFinalMasters_Assets_SourceAssetId",
                        column: x => x.SourceAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieFinalMasters_GenerationJobs_GenerationJobId",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieFinalMasters_MovieClips_SourceMovieClipId",
                        column: x => x.SourceMovieClipId,
                        principalTable: "MovieClips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieFinalMasters_MovieFinalMasters_SupersedesMasterId",
                        column: x => x.SupersedesMasterId,
                        principalTable: "MovieFinalMasters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieFinalMasters_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MovieFinalMasters_MovieShots_MovieShotId",
                        column: x => x.MovieShotId,
                        principalTable: "MovieShots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MovieFinalMasters_MovieTakes_SourceTakeId",
                        column: x => x.SourceTakeId,
                        principalTable: "MovieTakes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MovieFinalMasters_GenerationJobId",
                table: "MovieFinalMasters",
                column: "GenerationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieFinalMasters_MovieProjectId",
                table: "MovieFinalMasters",
                column: "MovieProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieFinalMasters_MovieShotId_RequestedAt",
                table: "MovieFinalMasters",
                columns: new[] { "MovieShotId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieFinalMasters_OutputAssetId",
                table: "MovieFinalMasters",
                column: "OutputAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieFinalMasters_RequestedByUserId",
                table: "MovieFinalMasters",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieFinalMasters_SourceAssetId",
                table: "MovieFinalMasters",
                column: "SourceAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieFinalMasters_SourceMovieClipId",
                table: "MovieFinalMasters",
                column: "SourceMovieClipId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieFinalMasters_SourceTakeId_TargetProfile_SupersededByMa~",
                table: "MovieFinalMasters",
                columns: new[] { "SourceTakeId", "TargetProfile", "SupersededByMasterId" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieFinalMasters_SupersedesMasterId",
                table: "MovieFinalMasters",
                column: "SupersedesMasterId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MovieFinalMasters");
        }
    }
}
