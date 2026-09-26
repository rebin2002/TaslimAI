using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Taslim.Api.Persistence;

#nullable disable

namespace Taslim.Api.Persistence.Migrations;

/// <inheritdoc />
[DbContext(typeof(TaslimDbContext))]
[Migration("20260926131500_AddMovieCinematographyFoundation")]
public partial class AddMovieCinematographyFoundation : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "CinematographyBibleReferencesJson",
            table: "MovieContinuityGuides",
            type: "character varying(20000)",
            maxLength: 20000,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CinematographyIntent",
            table: "MovieContinuityGuides",
            type: "character varying(30)",
            maxLength: 30,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CinematographyJson",
            table: "MovieShots",
            type: "character varying(20000)",
            maxLength: 20000,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "CinematographyBibleReferencesJson", table: "MovieContinuityGuides");
        migrationBuilder.DropColumn(name: "CinematographyIntent", table: "MovieContinuityGuides");
        migrationBuilder.DropColumn(name: "CinematographyJson", table: "MovieShots");
    }
}
