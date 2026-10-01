using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations;

public partial class AddMovieCharacterProductionSheets : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "MovieCharacterProductionSheets",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                MovieCharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                CurrentVersionNumber = table.Column<int>(type: "integer", nullable: false),
                Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                LockedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LockedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MovieCharacterProductionSheets", x => x.Id);
                table.ForeignKey("FK_MovieCharacterProductionSheets_AspNetUsers_ApprovedByUserId", x => x.ApprovedByUserId, "AspNetUsers", "Id", onDelete: ReferentialAction.SetNull);
                table.ForeignKey("FK_MovieCharacterProductionSheets_AspNetUsers_LockedByUserId", x => x.LockedByUserId, "AspNetUsers", "Id", onDelete: ReferentialAction.SetNull);
                table.ForeignKey("FK_MovieCharacterProductionSheets_MovieCharacters_MovieCharacterId", x => x.MovieCharacterId, "MovieCharacters", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "MovieCharacterProductionSheetVersions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                MovieCharacterProductionSheetId = table.Column<Guid>(type: "uuid", nullable: false),
                VersionNumber = table.Column<int>(type: "integer", nullable: false),
                Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                CanonicalIdentityFaceAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                BodyReferenceAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                FrontReferenceAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                SideReferenceAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                BackReferenceAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                SourceCharacterUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ProvenanceJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                ProvenanceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                LockedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LockedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MovieCharacterProductionSheetVersions", x => x.Id);
                table.ForeignKey("FK_MovieCharacterProductionSheetVersions_Assets_BackReferenceAssetId", x => x.BackReferenceAssetId, "Assets", "Id", onDelete: ReferentialAction.SetNull);
                table.ForeignKey("FK_MovieCharacterProductionSheetVersions_Assets_BodyReferenceAssetId", x => x.BodyReferenceAssetId, "Assets", "Id", onDelete: ReferentialAction.SetNull);
                table.ForeignKey("FK_MovieCharacterProductionSheetVersions_Assets_CanonicalIdentityFaceAssetId", x => x.CanonicalIdentityFaceAssetId, "Assets", "Id", onDelete: ReferentialAction.SetNull);
                table.ForeignKey("FK_MovieCharacterProductionSheetVersions_Assets_FrontReferenceAssetId", x => x.FrontReferenceAssetId, "Assets", "Id", onDelete: ReferentialAction.SetNull);
                table.ForeignKey("FK_MovieCharacterProductionSheetVersions_Assets_SideReferenceAssetId", x => x.SideReferenceAssetId, "Assets", "Id", onDelete: ReferentialAction.SetNull);
                table.ForeignKey("FK_MovieCharacterProductionSheetVersions_AspNetUsers_ApprovedByUserId", x => x.ApprovedByUserId, "AspNetUsers", "Id", onDelete: ReferentialAction.SetNull);
                table.ForeignKey("FK_MovieCharacterProductionSheetVersions_AspNetUsers_CreatedByUserId", x => x.CreatedByUserId, "AspNetUsers", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_MovieCharacterProductionSheetVersions_AspNetUsers_LockedByUserId", x => x.LockedByUserId, "AspNetUsers", "Id", onDelete: ReferentialAction.SetNull);
                table.ForeignKey("FK_MovieCharacterProductionSheetVersions_MovieCharacterProductionSheets_MovieCharacterProductionSheetId", x => x.MovieCharacterProductionSheetId, "MovieCharacterProductionSheets", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "MovieCharacterProductionSheetLooks",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                MovieCharacterProductionSheetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                Key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                Wardrobe = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                Appearance = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                ReferenceNotes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                ReferenceAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                SortOrder = table.Column<int>(type: "integer", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MovieCharacterProductionSheetLooks", x => x.Id);
                table.ForeignKey("FK_MovieCharacterProductionSheetLooks_Assets_ReferenceAssetId", x => x.ReferenceAssetId, "Assets", "Id", onDelete: ReferentialAction.SetNull);
                table.ForeignKey("FK_MovieCharacterProductionSheetLooks_MovieCharacterProductionSheetVersions_MovieCharacterProductionSheetVersionId", x => x.MovieCharacterProductionSheetVersionId, "MovieCharacterProductionSheetVersions", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(name: "IX_MovieCharacterProductionSheets_ApprovedByUserId", table: "MovieCharacterProductionSheets", column: "ApprovedByUserId");
        migrationBuilder.CreateIndex(name: "IX_MovieCharacterProductionSheets_LockedByUserId", table: "MovieCharacterProductionSheets", column: "LockedByUserId");
        migrationBuilder.CreateIndex(name: "IX_MovieCharacterProductionSheets_MovieCharacterId", table: "MovieCharacterProductionSheets", column: "MovieCharacterId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_MovieCharacterProductionSheets_MovieCharacterId_UpdatedAt", table: "MovieCharacterProductionSheets", columns: new[] { "MovieCharacterId", "UpdatedAt" });
        migrationBuilder.CreateIndex(name: "IX_MovieCharacterProductionSheetVersions_ApprovedByUserId", table: "MovieCharacterProductionSheetVersions", column: "ApprovedByUserId");
        migrationBuilder.CreateIndex(name: "IX_MovieCharacterProductionSheetVersions_BackReferenceAssetId", table: "MovieCharacterProductionSheetVersions", column: "BackReferenceAssetId");
        migrationBuilder.CreateIndex(name: "IX_MovieCharacterProductionSheetVersions_BodyReferenceAssetId", table: "MovieCharacterProductionSheetVersions", column: "BodyReferenceAssetId");
        migrationBuilder.CreateIndex(name: "IX_MovieCharacterProductionSheetVersions_CanonicalIdentityFaceAssetId", table: "MovieCharacterProductionSheetVersions", column: "CanonicalIdentityFaceAssetId");
        migrationBuilder.CreateIndex(name: "IX_MovieCharacterProductionSheetVersions_FrontReferenceAssetId", table: "MovieCharacterProductionSheetVersions", column: "FrontReferenceAssetId");
        migrationBuilder.CreateIndex(name: "IX_MovieCharacterProductionSheetVersions_LockedByUserId", table: "MovieCharacterProductionSheetVersions", column: "LockedByUserId");
        migrationBuilder.CreateIndex(name: "IX_MovieCharacterProductionSheetVersions_MovieCharacterProductionSheetId_VersionNumber", table: "MovieCharacterProductionSheetVersions", columns: new[] { "MovieCharacterProductionSheetId", "VersionNumber" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_MovieCharacterProductionSheetVersions_SideReferenceAssetId", table: "MovieCharacterProductionSheetVersions", column: "SideReferenceAssetId");
        migrationBuilder.CreateIndex(name: "IX_MovieCharacterProductionSheetLooks_MovieCharacterProductionSheetVersionId_Key", table: "MovieCharacterProductionSheetLooks", columns: new[] { "MovieCharacterProductionSheetVersionId", "Key" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_MovieCharacterProductionSheetLooks_ReferenceAssetId", table: "MovieCharacterProductionSheetLooks", column: "ReferenceAssetId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "MovieCharacterProductionSheetLooks");
        migrationBuilder.DropTable(name: "MovieCharacterProductionSheetVersions");
        migrationBuilder.DropTable(name: "MovieCharacterProductionSheets");
    }
}
