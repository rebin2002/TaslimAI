using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddResearchSourcesAndEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ResearchSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    StoredFileId = table.Column<Guid>(type: "uuid", nullable: true),
                    CitationId = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CanonicalUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Domain = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Publisher = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RetrievedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SourceType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Snippet = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ExtractedText = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    SearchQuery = table.Column<string>(type: "character varying(800)", maxLength: 800, nullable: true),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    IsSelected = table.Column<bool>(type: "boolean", nullable: false),
                    MetadataJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResearchSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResearchSources_GenerationJobs_GenerationJobId",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ResearchSources_StoredFiles_StoredFileId",
                        column: x => x.StoredFileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ResearchSources_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ResearchEvidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResearchSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Topic = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    Excerpt = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Context = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResearchEvidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResearchEvidence_GenerationJobs_GenerationJobId",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ResearchEvidence_ResearchSources_ResearchSourceId",
                        column: x => x.ResearchSourceId,
                        principalTable: "ResearchSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ResearchEvidence_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResearchEvidence_GenerationJobId_ResearchSourceId",
                table: "ResearchEvidence",
                columns: new[] { "GenerationJobId", "ResearchSourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_ResearchEvidence_ResearchSourceId",
                table: "ResearchEvidence",
                column: "ResearchSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_ResearchEvidence_WorkspaceId",
                table: "ResearchEvidence",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_ResearchSources_GenerationJobId_CitationId",
                table: "ResearchSources",
                columns: new[] { "GenerationJobId", "CitationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResearchSources_StoredFileId",
                table: "ResearchSources",
                column: "StoredFileId");

            migrationBuilder.CreateIndex(
                name: "IX_ResearchSources_WorkspaceId_RetrievedAt",
                table: "ResearchSources",
                columns: new[] { "WorkspaceId", "RetrievedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResearchEvidence");

            migrationBuilder.DropTable(
                name: "ResearchSources");
        }
    }
}
