using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieWorldVariations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MovieSetVariations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieSetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    VisualDescription = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    TimeOfDay = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Weather = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    Lighting = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ContinuityNotes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ReferenceAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieSetVariations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieSetVariations_Assets_ReferenceAssetId",
                        column: x => x.ReferenceAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieSetVariations_MovieSets_MovieSetId",
                        column: x => x.MovieSetId,
                        principalTable: "MovieSets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MovieSetVariations_MovieSetId_IsDefault",
                table: "MovieSetVariations",
                columns: new[] { "MovieSetId", "IsDefault" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieSetVariations_MovieSetId_Name",
                table: "MovieSetVariations",
                columns: new[] { "MovieSetId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieSetVariations_ReferenceAssetId",
                table: "MovieSetVariations",
                column: "ReferenceAssetId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MovieSetVariations");
        }
    }
}
