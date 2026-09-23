using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieStudioFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MovieProjects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: false),
                    AspectRatio = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Style = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Language = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    AdditionalInstructions = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieProjects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieProjects_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieProjects_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieProjects_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MovieAssemblies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    OutputFormat = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MetadataJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieAssemblies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieAssemblies_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieAssemblies_GenerationJobs_GenerationJobId",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieAssemblies_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieCharacters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Appearance = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    VoiceAndPerformance = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ContinuityNotes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ReferenceAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieCharacters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieCharacters_Assets_ReferenceAssetId",
                        column: x => x.ReferenceAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieCharacters_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieContinuityGuides",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    VisualLanguage = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CameraLanguage = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ColorAndLighting = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    SoundAndNarration = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ContinuityRules = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    ReferenceAssetIdsJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieContinuityGuides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieContinuityGuides_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieLocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    VisualContinuityNotes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ReferenceAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieLocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieLocations_Assets_ReferenceAssetId",
                        column: x => x.ReferenceAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieLocations_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieScenes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Summary = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: true),
                    ContinuityNotes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Narration = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    Dialogue = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieScenes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieScenes_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieShots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieSceneId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    CameraAndFraming = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CameraMotion = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: true),
                    Narration = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    Dialogue = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    VisualContinuityNotes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieShots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieShots_MovieScenes_MovieSceneId",
                        column: x => x.MovieSceneId,
                        principalTable: "MovieScenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieClips",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieShotId = table.Column<Guid>(type: "uuid", nullable: true),
                    GenerationJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    StoredFileId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ProviderClipId = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: true),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: true),
                    MetadataJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieClips", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieClips_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieClips_GenerationJobs_GenerationJobId",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieClips_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MovieClips_MovieShots_MovieShotId",
                        column: x => x.MovieShotId,
                        principalTable: "MovieShots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieClips_StoredFiles_StoredFileId",
                        column: x => x.StoredFileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MovieAssemblies_AssetId",
                table: "MovieAssemblies",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieAssemblies_GenerationJobId",
                table: "MovieAssemblies",
                column: "GenerationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieAssemblies_MovieProjectId_CreatedAt",
                table: "MovieAssemblies",
                columns: new[] { "MovieProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieCharacters_MovieProjectId",
                table: "MovieCharacters",
                column: "MovieProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieCharacters_ReferenceAssetId",
                table: "MovieCharacters",
                column: "ReferenceAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieClips_AssetId",
                table: "MovieClips",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieClips_GenerationJobId",
                table: "MovieClips",
                column: "GenerationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieClips_MovieProjectId_Status",
                table: "MovieClips",
                columns: new[] { "MovieProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieClips_MovieShotId",
                table: "MovieClips",
                column: "MovieShotId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieClips_StoredFileId",
                table: "MovieClips",
                column: "StoredFileId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieContinuityGuides_MovieProjectId",
                table: "MovieContinuityGuides",
                column: "MovieProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieLocations_MovieProjectId",
                table: "MovieLocations",
                column: "MovieProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieLocations_ReferenceAssetId",
                table: "MovieLocations",
                column: "ReferenceAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProjects_CreatedByUserId",
                table: "MovieProjects",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProjects_ProjectId",
                table: "MovieProjects",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProjects_WorkspaceId_UpdatedAt",
                table: "MovieProjects",
                columns: new[] { "WorkspaceId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieScenes_MovieProjectId_Sequence",
                table: "MovieScenes",
                columns: new[] { "MovieProjectId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieShots_MovieSceneId_Sequence",
                table: "MovieShots",
                columns: new[] { "MovieSceneId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MovieAssemblies");

            migrationBuilder.DropTable(
                name: "MovieCharacters");

            migrationBuilder.DropTable(
                name: "MovieClips");

            migrationBuilder.DropTable(
                name: "MovieContinuityGuides");

            migrationBuilder.DropTable(
                name: "MovieLocations");

            migrationBuilder.DropTable(
                name: "MovieShots");

            migrationBuilder.DropTable(
                name: "MovieScenes");

            migrationBuilder.DropTable(
                name: "MovieProjects");
        }
    }
}
