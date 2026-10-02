using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieWave5PropBible : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MoviePropBibles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    MoviePropId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdentityKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Role = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    VisualIdentity = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    ContinuityRules = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    ApprovalState = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CurrentVersionNumber = table.Column<int>(type: "integer", nullable: false),
                    CurrentVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedVersionNumber = table.Column<int>(type: "integer", nullable: true),
                    ApprovedVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    LockedVersionNumber = table.Column<int>(type: "integer", nullable: true),
                    LockedVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    LockedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LockedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MoviePropBibles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MoviePropBibles_AspNetUsers_ApprovedByUserId",
                        column: x => x.ApprovedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MoviePropBibles_AspNetUsers_LockedByUserId",
                        column: x => x.LockedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MoviePropBibles_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MoviePropBibles_MovieProps_MoviePropId",
                        column: x => x.MoviePropId,
                        principalTable: "MovieProps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MoviePropBibleReferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MoviePropBibleId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ProvenanceJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MoviePropBibleReferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MoviePropBibleReferences_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MoviePropBibleReferences_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MoviePropBibleReferences_MoviePropBibles_MoviePropBibleId",
                        column: x => x.MoviePropBibleId,
                        principalTable: "MoviePropBibles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MoviePropBibleVariants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MoviePropBibleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Label = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    State = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    VisualNotes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ReferenceAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProvenanceJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MoviePropBibleVariants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MoviePropBibleVariants_Assets_ReferenceAssetId",
                        column: x => x.ReferenceAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MoviePropBibleVariants_MoviePropBibles_MoviePropBibleId",
                        column: x => x.MoviePropBibleId,
                        principalTable: "MoviePropBibles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MoviePropBibleVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MoviePropBibleId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    IdentityKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Role = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    VisualIdentity = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    ContinuityRules = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    ReferencesJson = table.Column<string>(type: "character varying(30000)", maxLength: 30000, nullable: false),
                    VariantsJson = table.Column<string>(type: "character varying(30000)", maxLength: 30000, nullable: false),
                    UsagesJson = table.Column<string>(type: "character varying(40000)", maxLength: 40000, nullable: false),
                    ProvenanceJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReviewReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    LockedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LockedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MoviePropBibleVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MoviePropBibleVersions_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MoviePropBibleVersions_AspNetUsers_LockedByUserId",
                        column: x => x.LockedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MoviePropBibleVersions_AspNetUsers_ReviewedByUserId",
                        column: x => x.ReviewedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MoviePropBibleVersions_MoviePropBibles_MoviePropBibleId",
                        column: x => x.MoviePropBibleId,
                        principalTable: "MoviePropBibles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MoviePropBibleReferences_AssetId",
                table: "MoviePropBibleReferences",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MoviePropBibleReferences_CreatedByUserId",
                table: "MoviePropBibleReferences",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MoviePropBibleReferences_MoviePropBibleId_AssetId_Role",
                table: "MoviePropBibleReferences",
                columns: new[] { "MoviePropBibleId", "AssetId", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MoviePropBibles_ApprovedByUserId",
                table: "MoviePropBibles",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MoviePropBibles_LockedByUserId",
                table: "MoviePropBibles",
                column: "LockedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MoviePropBibles_MovieProjectId_ApprovalState",
                table: "MoviePropBibles",
                columns: new[] { "MovieProjectId", "ApprovalState" });

            migrationBuilder.CreateIndex(
                name: "IX_MoviePropBibles_MovieProjectId_MoviePropId",
                table: "MoviePropBibles",
                columns: new[] { "MovieProjectId", "MoviePropId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MoviePropBibles_MoviePropId",
                table: "MoviePropBibles",
                column: "MoviePropId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MoviePropBibleVariants_MoviePropBibleId_Key",
                table: "MoviePropBibleVariants",
                columns: new[] { "MoviePropBibleId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MoviePropBibleVariants_ReferenceAssetId",
                table: "MoviePropBibleVariants",
                column: "ReferenceAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MoviePropBibleVersions_CreatedByUserId",
                table: "MoviePropBibleVersions",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MoviePropBibleVersions_LockedByUserId",
                table: "MoviePropBibleVersions",
                column: "LockedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MoviePropBibleVersions_MoviePropBibleId_Status",
                table: "MoviePropBibleVersions",
                columns: new[] { "MoviePropBibleId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MoviePropBibleVersions_MoviePropBibleId_VersionNumber",
                table: "MoviePropBibleVersions",
                columns: new[] { "MoviePropBibleId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MoviePropBibleVersions_ReviewedByUserId",
                table: "MoviePropBibleVersions",
                column: "ReviewedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MoviePropBibleReferences");

            migrationBuilder.DropTable(
                name: "MoviePropBibleVariants");

            migrationBuilder.DropTable(
                name: "MoviePropBibleVersions");

            migrationBuilder.DropTable(
                name: "MoviePropBibles");
        }
    }
}
