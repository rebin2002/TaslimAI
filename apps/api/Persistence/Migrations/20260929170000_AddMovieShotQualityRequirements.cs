using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Taslim.Api.Persistence;

#nullable disable

namespace Taslim.Api.Persistence.Migrations;

[DbContext(typeof(TaslimDbContext))]
[Migration("20260929170000_AddMovieShotQualityRequirements")]
public partial class AddMovieShotQualityRequirements : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "QualityRequirementsJson",
            table: "MovieShots",
            type: "character varying(24000)",
            maxLength: 24000,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "QualityRequirementsJson",
            table: "MovieShots");
    }
}
