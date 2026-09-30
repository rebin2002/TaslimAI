using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGeneratedMediaIngestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContainerFormat",
                table: "StoredFiles",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentHashSha256",
                table: "StoredFiles",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "StoredFiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "DurationSeconds",
                table: "StoredFiles",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Height",
                table: "StoredFiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RetainUntil",
                table: "StoredFiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Width",
                table: "StoredFiles",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GeneratedMediaProvenance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    StoredFileId = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    MovieTakeId = table.Column<Guid>(type: "uuid", nullable: true),
                    MovieClipId = table.Column<Guid>(type: "uuid", nullable: true),
                    OutputType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    IngestionKey = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    ContentHashSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ParentContentHashSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ChainHashSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SafeMetadataJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RetainUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CleanedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CleanupReason = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GeneratedMediaProvenance", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GeneratedMediaProvenance_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_GeneratedMediaProvenance_GenerationJobs_GenerationJobId",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GeneratedMediaProvenance_MovieClips_MovieClipId",
                        column: x => x.MovieClipId,
                        principalTable: "MovieClips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_GeneratedMediaProvenance_MovieTakes_MovieTakeId",
                        column: x => x.MovieTakeId,
                        principalTable: "MovieTakes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_GeneratedMediaProvenance_StoredFiles_StoredFileId",
                        column: x => x.StoredFileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GeneratedMediaProvenance_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StoredFiles_WorkspaceId_ContentHashSha256",
                table: "StoredFiles",
                columns: new[] { "WorkspaceId", "ContentHashSha256" },
                unique: true,
                filter: "\"ContentHashSha256\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedMediaProvenance_AssetId",
                table: "GeneratedMediaProvenance",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedMediaProvenance_GenerationJobId",
                table: "GeneratedMediaProvenance",
                column: "GenerationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedMediaProvenance_IngestionKey",
                table: "GeneratedMediaProvenance",
                column: "IngestionKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedMediaProvenance_MovieClipId",
                table: "GeneratedMediaProvenance",
                column: "MovieClipId");

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedMediaProvenance_MovieTakeId",
                table: "GeneratedMediaProvenance",
                column: "MovieTakeId");

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedMediaProvenance_StoredFileId",
                table: "GeneratedMediaProvenance",
                column: "StoredFileId");

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedMediaProvenance_WorkspaceId_CreatedAt",
                table: "GeneratedMediaProvenance",
                columns: new[] { "WorkspaceId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GeneratedMediaProvenance");

            migrationBuilder.DropIndex(
                name: "IX_StoredFiles_WorkspaceId_ContentHashSha256",
                table: "StoredFiles");

            migrationBuilder.DropColumn(
                name: "ContainerFormat",
                table: "StoredFiles");

            migrationBuilder.DropColumn(
                name: "ContentHashSha256",
                table: "StoredFiles");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "StoredFiles");

            migrationBuilder.DropColumn(
                name: "DurationSeconds",
                table: "StoredFiles");

            migrationBuilder.DropColumn(
                name: "Height",
                table: "StoredFiles");

            migrationBuilder.DropColumn(
                name: "RetainUntil",
                table: "StoredFiles");

            migrationBuilder.DropColumn(
                name: "Width",
                table: "StoredFiles");
        }
    }
}
