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
        migrationBuilder.AlterColumn<string>(
            name: "QualityRequirementsJson",
            table: "MovieShots",
            type: "character varying(24000)",
            maxLength: 24000,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "character varying(20000)",
            oldMaxLength: 20000,
            oldNullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "QualityRequirementsJson",
            table: "MovieShots",
            type: "character varying(20000)",
            maxLength: 20000,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "character varying(24000)",
            oldMaxLength: 24000,
            oldNullable: true);
    }
}
