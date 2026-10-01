using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieLocationGeographySheets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MovieLocationGeographySheets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieLocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EstablishingReferenceAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    EstablishingReferenceNotes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    WideThreeQuarterReferenceAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    WideThreeQuarterReferenceNotes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    EntrancesExitsJson = table.Column<string>(type: "character varying(60000)", maxLength: 60000, nullable: false),
                    WindowsJson = table.Column<string>(type: "character varying(60000)", maxLength: 60000, nullable: false),
                    PathsJson = table.Column<string>(type: "character varying(60000)", maxLength: 60000, nullable: false),
                    MajorObjectsJson = table.Column<string>(type: "character varying(60000)", maxLength: 60000, nullable: false),
                    LightSourcesJson = table.Column<string>(type: "character varying(60000)", maxLength: 60000, nullable: false),
                    OrientationAnchorsJson = table.Column<string>(type: "character varying(60000)", maxLength: 60000, nullable: false),
                    GuideRevisionNumber = table.Column<int>(type: "integer", nullable: false),
                    WorldBibleJson = table.Column<string>(type: "character varying(60000)", maxLength: 60000, nullable: true),
                    VisualBibleJson = table.Column<string>(type: "character varying(60000)", maxLength: 60000, nullable: true),
                    ContinuitySnapshotHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieLocationGeographySheets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieLocationGeographySheets_AspNetUsers_ApprovedByUserId",
                        column: x => x.ApprovedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieLocationGeographySheets_Assets_EstablishingReferenceAs~",
                        column: x => x.EstablishingReferenceAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieLocationGeographySheets_Assets_WideThreeQuarterReferen~",
                        column: x => x.WideThreeQuarterReferenceAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieLocationGeographySheets_MovieLocations_MovieLocationId",
                        column: x => x.MovieLocationId,
                        principalTable: "MovieLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieLocationGeographyVariants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieLocationGeographySheetId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    TimeOfDay = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Weather = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    Lighting = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ColorPalette = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ReferenceAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    ContinuityNotes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    GuideRevisionNumber = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieLocationGeographyVariants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieLocationGeographyVariants_AspNetUsers_ApprovedByUserId",
                        column: x => x.ApprovedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieLocationGeographyVariants_Assets_ReferenceAssetId",
                        column: x => x.ReferenceAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieLocationGeographyVariants_MovieLocationGeographySheets~",
                        column: x => x.MovieLocationGeographySheetId,
                        principalTable: "MovieLocationGeographySheets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MovieLocationGeographySheets_ApprovedByUserId",
                table: "MovieLocationGeographySheets",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieLocationGeographySheets_EstablishingReferenceAssetId",
                table: "MovieLocationGeographySheets",
                column: "EstablishingReferenceAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieLocationGeographySheets_MovieLocationId",
                table: "MovieLocationGeographySheets",
                column: "MovieLocationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieLocationGeographySheets_Status_UpdatedAt",
                table: "MovieLocationGeographySheets",
                columns: new[] { "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieLocationGeographySheets_WideThreeQuarterReferenceAsset~",
                table: "MovieLocationGeographySheets",
                column: "WideThreeQuarterReferenceAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieLocationGeographyVariants_ApprovedByUserId",
                table: "MovieLocationGeographyVariants",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieLocationGeographyVariants_MovieLocationGeographySheet~1",
                table: "MovieLocationGeographyVariants",
                columns: new[] { "MovieLocationGeographySheetId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieLocationGeographyVariants_MovieLocationGeographySheetI~",
                table: "MovieLocationGeographyVariants",
                columns: new[] { "MovieLocationGeographySheetId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieLocationGeographyVariants_ReferenceAssetId",
                table: "MovieLocationGeographyVariants",
                column: "ReferenceAssetId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MovieLocationGeographyVariants");

            migrationBuilder.DropTable(
                name: "MovieLocationGeographySheets");
        }
    }
}
