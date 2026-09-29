using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieShotProductionContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContinuitySensitivity",
                table: "MovieShots",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NarrativeImportance",
                table: "MovieShots",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProductionComplexityJson",
                table: "MovieShots",
                type: "character varying(20000)",
                maxLength: 20000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QualityRequirementsJson",
                table: "MovieShots",
                type: "character varying(20000)",
                maxLength: 20000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetOutputRequirementsJson",
                table: "MovieShots",
                type: "character varying(20000)",
                maxLength: 20000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpscaleSuitability",
                table: "MovieShots",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContinuitySensitivity",
                table: "MovieShots");

            migrationBuilder.DropColumn(
                name: "NarrativeImportance",
                table: "MovieShots");

            migrationBuilder.DropColumn(
                name: "ProductionComplexityJson",
                table: "MovieShots");

            migrationBuilder.DropColumn(
                name: "QualityRequirementsJson",
                table: "MovieShots");

            migrationBuilder.DropColumn(
                name: "TargetOutputRequirementsJson",
                table: "MovieShots");

            migrationBuilder.DropColumn(
                name: "UpscaleSuitability",
                table: "MovieShots");
        }
    }
}
