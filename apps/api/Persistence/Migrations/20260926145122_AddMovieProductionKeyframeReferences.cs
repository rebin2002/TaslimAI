using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieProductionKeyframeReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CinematographyReferenceJson",
                table: "MovieProductionVersions",
                type: "character varying(20000)",
                maxLength: 20000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContinuitySnapshotReferenceJson",
                table: "MovieProductionVersions",
                type: "character varying(20000)",
                maxLength: 20000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CinematographyReferenceJson",
                table: "MovieProductionVersions");

            migrationBuilder.DropColumn(
                name: "ContinuitySnapshotReferenceJson",
                table: "MovieProductionVersions");
        }
    }
}
