using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Taslim.Api.Persistence;

#nullable disable

namespace Taslim.Api.Persistence.Migrations;

[DbContext(typeof(TaslimDbContext))]
[Migration("20260924210000_AddProductionObservability")]
public partial class AddProductionObservability : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "RequestId",
            table: "GenerationJobs",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "RetryCount",
            table: "GenerationJobs",
            type: "integer",
            nullable: false,
            defaultValue: 0);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "RequestId",
            table: "GenerationJobs");

        migrationBuilder.DropColumn(
            name: "RetryCount",
            table: "GenerationJobs");
    }
}
