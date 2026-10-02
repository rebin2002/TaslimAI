using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieProductionKit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MovieProductionKits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    CurrentRevisionNumber = table.Column<int>(type: "integer", nullable: false),
                    LockedRevisionNumber = table.Column<int>(type: "integer", nullable: true),
                    LockedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LockedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieProductionKits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieProductionKits_AspNetUsers_LockedByUserId",
                        column: x => x.LockedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieProductionKits_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieProductionKitRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProductionKitId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionNumber = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SourceGuideRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceGuideRevisionNumber = table.Column<int>(type: "integer", nullable: false),
                    SourceGuideHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ReviewNote = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LockedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    LockedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevisionHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieProductionKitRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieProductionKitRevisions_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieProductionKitRevisions_AspNetUsers_LockedByUserId",
                        column: x => x.LockedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieProductionKitRevisions_AspNetUsers_ReviewedByUserId",
                        column: x => x.ReviewedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieProductionKitRevisions_MovieGuideRevisions_SourceGuide~",
                        column: x => x.SourceGuideRevisionId,
                        principalTable: "MovieGuideRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieProductionKitRevisions_MovieProductionKits_MovieProduc~",
                        column: x => x.MovieProductionKitId,
                        principalTable: "MovieProductionKits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieProductionKitReferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProductionKitRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReferenceType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceRevision = table.Column<int>(type: "integer", nullable: true),
                    Label = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Role = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false),
                    SourceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProvenanceJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieProductionKitReferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieProductionKitReferences_MovieProductionKitRevisions_Mo~",
                        column: x => x.MovieProductionKitRevisionId,
                        principalTable: "MovieProductionKitRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionKitReferences_MovieProductionKitRevisionId_R~",
                table: "MovieProductionKitReferences",
                columns: new[] { "MovieProductionKitRevisionId", "ReferenceType", "SourceId", "SourceRevision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionKitReferences_MovieProductionKitRevisionId_S~",
                table: "MovieProductionKitReferences",
                columns: new[] { "MovieProductionKitRevisionId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionKitReferences_ReferenceType_SourceId",
                table: "MovieProductionKitReferences",
                columns: new[] { "ReferenceType", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionKitRevisions_CreatedByUserId",
                table: "MovieProductionKitRevisions",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionKitRevisions_LockedByUserId",
                table: "MovieProductionKitRevisions",
                column: "LockedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionKitRevisions_MovieProductionKitId_RevisionNu~",
                table: "MovieProductionKitRevisions",
                columns: new[] { "MovieProductionKitId", "RevisionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionKitRevisions_MovieProductionKitId_Status",
                table: "MovieProductionKitRevisions",
                columns: new[] { "MovieProductionKitId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionKitRevisions_ReviewedByUserId",
                table: "MovieProductionKitRevisions",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionKitRevisions_SourceGuideRevisionId",
                table: "MovieProductionKitRevisions",
                column: "SourceGuideRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionKits_LockedByUserId",
                table: "MovieProductionKits",
                column: "LockedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionKits_MovieProjectId",
                table: "MovieProductionKits",
                column: "MovieProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionKits_MovieProjectId_UpdatedAt",
                table: "MovieProductionKits",
                columns: new[] { "MovieProjectId", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MovieProductionKitReferences");

            migrationBuilder.DropTable(
                name: "MovieProductionKitRevisions");

            migrationBuilder.DropTable(
                name: "MovieProductionKits");
        }
    }
}
