using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Taslim.Api.Persistence;

#nullable disable

namespace Taslim.Api.Persistence.Migrations;

[DbContext(typeof(TaslimDbContext))]
[Migration("20260926140000_AddMovieShotPlanning")]
public partial class AddMovieShotPlanning : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "ContinuityReferences", table: "MovieShots", type: "character varying(4000)", maxLength: 4000, nullable: true);
        migrationBuilder.AddColumn<string>(name: "LocationSet", table: "MovieShots", type: "character varying(2000)", maxLength: 2000, nullable: true);
        migrationBuilder.AddColumn<string>(name: "ProductionRequirements", table: "MovieShots", type: "character varying(4000)", maxLength: 4000, nullable: true);
        migrationBuilder.AddColumn<string>(name: "Purpose", table: "MovieShots", type: "character varying(2000)", maxLength: 2000, nullable: true);
        migrationBuilder.AddColumn<string>(name: "SubjectCharacterIdsJson", table: "MovieShots", type: "character varying(8000)", maxLength: 8000, nullable: true);
        migrationBuilder.AddColumn<string>(name: "Subjects", table: "MovieShots", type: "character varying(4000)", maxLength: 4000, nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ContinuityReferences", table: "MovieShots");
        migrationBuilder.DropColumn(name: "LocationSet", table: "MovieShots");
        migrationBuilder.DropColumn(name: "ProductionRequirements", table: "MovieShots");
        migrationBuilder.DropColumn(name: "Purpose", table: "MovieShots");
        migrationBuilder.DropColumn(name: "SubjectCharacterIdsJson", table: "MovieShots");
        migrationBuilder.DropColumn(name: "Subjects", table: "MovieShots");
    }
}
