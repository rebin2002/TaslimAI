using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieStudioV2CoreDomain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MovieScenes_MovieProjectId_Sequence",
                table: "MovieScenes");

            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAt",
                table: "MovieShots",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FinalTakeId",
                table: "MovieShots",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SelectedTakeId",
                table: "MovieShots",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "MovieShots",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Planned");

            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAt",
                table: "MovieScenes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MovieSequenceId",
                table: "MovieScenes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "MovieScenes",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Planned");

            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAt",
                table: "MovieProjects",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AutoDirectorEnabled",
                table: "MovieProjects",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ProductionStatus",
                table: "MovieProjects",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "Draft");

            migrationBuilder.AddColumn<string>(
                name: "QualityLevel",
                table: "MovieProjects",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Standard");

            migrationBuilder.AddColumn<DateTime>(
                name: "StatusChangedAt",
                table: "MovieProjects",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StatusChangedByUserId",
                table: "MovieProjects",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MovieActs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Summary = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false, defaultValue: "Planned"),
                    StatusChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StatusChangedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ArchivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ArchivedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieActs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieActs_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieTakes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieShotId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Label = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false, defaultValue: "Draft"),
                    QualityLevel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Standard"),
                    AutoDirectorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    MovieClipId = table.Column<Guid>(type: "uuid", nullable: true),
                    GenerationJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    MetadataJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    SelectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SelectedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    FinalizedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinalizedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    StatusChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StatusChangedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ArchivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ArchivedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieTakes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieTakes_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieTakes_GenerationJobs_GenerationJobId",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieTakes_MovieClips_MovieClipId",
                        column: x => x.MovieClipId,
                        principalTable: "MovieClips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieTakes_MovieShots_MovieShotId",
                        column: x => x.MovieShotId,
                        principalTable: "MovieShots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieSequences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieActId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Summary = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false, defaultValue: "Planned"),
                    StatusChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StatusChangedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ArchivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ArchivedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieSequences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieSequences_MovieActs_MovieActId",
                        column: x => x.MovieActId,
                        principalTable: "MovieActs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieTakeApprovals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieTakeId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Decision = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Comment = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieTakeApprovals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieTakeApprovals_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieTakeApprovals_MovieTakes_MovieTakeId",
                        column: x => x.MovieTakeId,
                        principalTable: "MovieTakes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MovieShots_FinalTakeId",
                table: "MovieShots",
                column: "FinalTakeId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieShots_SelectedTakeId",
                table: "MovieShots",
                column: "SelectedTakeId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieScenes_MovieProjectId",
                table: "MovieScenes",
                column: "MovieProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieScenes_MovieSequenceId",
                table: "MovieScenes",
                column: "MovieSequenceId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieScenes_MovieSequenceId_Sequence",
                table: "MovieScenes",
                columns: new[] { "MovieSequenceId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieActs_MovieProjectId_Sequence",
                table: "MovieActs",
                columns: new[] { "MovieProjectId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieSequences_MovieActId_Sequence",
                table: "MovieSequences",
                columns: new[] { "MovieActId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieTakeApprovals_MovieTakeId_CreatedAt",
                table: "MovieTakeApprovals",
                columns: new[] { "MovieTakeId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieTakeApprovals_UserId",
                table: "MovieTakeApprovals",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieTakes_AssetId",
                table: "MovieTakes",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieTakes_GenerationJobId",
                table: "MovieTakes",
                column: "GenerationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieTakes_MovieClipId",
                table: "MovieTakes",
                column: "MovieClipId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieTakes_MovieShotId_Status",
                table: "MovieTakes",
                columns: new[] { "MovieShotId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieTakes_MovieShotId_VersionNumber",
                table: "MovieTakes",
                columns: new[] { "MovieShotId", "VersionNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_MovieScenes_MovieSequences_MovieSequenceId",
                table: "MovieScenes",
                column: "MovieSequenceId",
                principalTable: "MovieSequences",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_MovieShots_MovieTakes_FinalTakeId",
                table: "MovieShots",
                column: "FinalTakeId",
                principalTable: "MovieTakes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_MovieShots_MovieTakes_SelectedTakeId",
                table: "MovieShots",
                column: "SelectedTakeId",
                principalTable: "MovieTakes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MovieScenes_MovieSequences_MovieSequenceId",
                table: "MovieScenes");

            migrationBuilder.DropForeignKey(
                name: "FK_MovieShots_MovieTakes_FinalTakeId",
                table: "MovieShots");

            migrationBuilder.DropForeignKey(
                name: "FK_MovieShots_MovieTakes_SelectedTakeId",
                table: "MovieShots");

            migrationBuilder.DropTable(
                name: "MovieSequences");

            migrationBuilder.DropTable(
                name: "MovieTakeApprovals");

            migrationBuilder.DropTable(
                name: "MovieActs");

            migrationBuilder.DropTable(
                name: "MovieTakes");

            migrationBuilder.DropIndex(
                name: "IX_MovieShots_FinalTakeId",
                table: "MovieShots");

            migrationBuilder.DropIndex(
                name: "IX_MovieShots_SelectedTakeId",
                table: "MovieShots");

            migrationBuilder.DropIndex(
                name: "IX_MovieScenes_MovieProjectId",
                table: "MovieScenes");

            migrationBuilder.DropIndex(
                name: "IX_MovieScenes_MovieSequenceId",
                table: "MovieScenes");

            migrationBuilder.DropIndex(
                name: "IX_MovieScenes_MovieSequenceId_Sequence",
                table: "MovieScenes");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "MovieShots");

            migrationBuilder.DropColumn(
                name: "FinalTakeId",
                table: "MovieShots");

            migrationBuilder.DropColumn(
                name: "SelectedTakeId",
                table: "MovieShots");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "MovieShots");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "MovieScenes");

            migrationBuilder.DropColumn(
                name: "MovieSequenceId",
                table: "MovieScenes");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "MovieScenes");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "MovieProjects");

            migrationBuilder.DropColumn(
                name: "AutoDirectorEnabled",
                table: "MovieProjects");

            migrationBuilder.DropColumn(
                name: "ProductionStatus",
                table: "MovieProjects");

            migrationBuilder.DropColumn(
                name: "QualityLevel",
                table: "MovieProjects");

            migrationBuilder.DropColumn(
                name: "StatusChangedAt",
                table: "MovieProjects");

            migrationBuilder.DropColumn(
                name: "StatusChangedByUserId",
                table: "MovieProjects");

            migrationBuilder.CreateIndex(
                name: "IX_MovieScenes_MovieProjectId_Sequence",
                table: "MovieScenes",
                columns: new[] { "MovieProjectId", "Sequence" },
                unique: true);
        }
    }
}
