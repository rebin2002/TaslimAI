using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieSoundTracks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MovieSoundLibraryReferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieSoundLibraryReferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieSoundLibraryReferences_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieSoundLibraryReferences_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieSoundLibraryReferences_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieSoundTracks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieSceneId = table.Column<Guid>(type: "uuid", nullable: true),
                    MovieShotId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    GenerationJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    LibraryReferenceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Layer = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    StartMilliseconds = table.Column<int>(type: "integer", nullable: false),
                    EndMilliseconds = table.Column<int>(type: "integer", nullable: false),
                    FadeInMilliseconds = table.Column<int>(type: "integer", nullable: false),
                    FadeOutMilliseconds = table.Column<int>(type: "integer", nullable: false),
                    GainDb = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SourceKind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ProvenanceJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieSoundTracks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieSoundTracks_AspNetUsers_ApprovedByUserId",
                        column: x => x.ApprovedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieSoundTracks_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieSoundTracks_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieSoundTracks_GenerationJobs_GenerationJobId",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieSoundTracks_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MovieSoundTracks_MovieScenes_MovieSceneId",
                        column: x => x.MovieSceneId,
                        principalTable: "MovieScenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MovieSoundTracks_MovieShots_MovieShotId",
                        column: x => x.MovieShotId,
                        principalTable: "MovieShots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MovieSoundTracks_MovieSoundLibraryReferences_LibraryReferen~",
                        column: x => x.LibraryReferenceId,
                        principalTable: "MovieSoundLibraryReferences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MovieSoundApprovals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieSoundTrackId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Decision = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieSoundApprovals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieSoundApprovals_AspNetUsers_ReviewerUserId",
                        column: x => x.ReviewerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieSoundApprovals_MovieSoundTracks_MovieSoundTrackId",
                        column: x => x.MovieSoundTrackId,
                        principalTable: "MovieSoundTracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundApprovals_MovieSoundTrackId_CreatedAt",
                table: "MovieSoundApprovals",
                columns: new[] { "MovieSoundTrackId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundApprovals_ReviewerUserId",
                table: "MovieSoundApprovals",
                column: "ReviewerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundLibraryReferences_AssetId",
                table: "MovieSoundLibraryReferences",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundLibraryReferences_CreatedByUserId",
                table: "MovieSoundLibraryReferences",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundLibraryReferences_MovieProjectId_AssetId",
                table: "MovieSoundLibraryReferences",
                columns: new[] { "MovieProjectId", "AssetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundLibraryReferences_MovieProjectId_UpdatedAt",
                table: "MovieSoundLibraryReferences",
                columns: new[] { "MovieProjectId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundTracks_ApprovedByUserId",
                table: "MovieSoundTracks",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundTracks_AssetId",
                table: "MovieSoundTracks",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundTracks_CreatedByUserId",
                table: "MovieSoundTracks",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundTracks_GenerationJobId",
                table: "MovieSoundTracks",
                column: "GenerationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundTracks_LibraryReferenceId",
                table: "MovieSoundTracks",
                column: "LibraryReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundTracks_MovieProjectId_MovieSceneId_StartMilliseco~",
                table: "MovieSoundTracks",
                columns: new[] { "MovieProjectId", "MovieSceneId", "StartMilliseconds" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundTracks_MovieProjectId_MovieShotId_StartMillisecon~",
                table: "MovieSoundTracks",
                columns: new[] { "MovieProjectId", "MovieShotId", "StartMilliseconds" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundTracks_MovieProjectId_Status",
                table: "MovieSoundTracks",
                columns: new[] { "MovieProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundTracks_MovieSceneId",
                table: "MovieSoundTracks",
                column: "MovieSceneId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieSoundTracks_MovieShotId",
                table: "MovieSoundTracks",
                column: "MovieShotId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MovieSoundApprovals");

            migrationBuilder.DropTable(
                name: "MovieSoundTracks");

            migrationBuilder.DropTable(
                name: "MovieSoundLibraryReferences");
        }
    }
}
