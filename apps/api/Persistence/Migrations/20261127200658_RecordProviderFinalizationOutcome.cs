using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecordProviderFinalizationOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ResultCategory",
                table: "ProviderExecutionFinalizations",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResultCode",
                table: "ProviderExecutionFinalizations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResultCategory",
                table: "ProviderExecutionFinalizations");

            migrationBuilder.DropColumn(
                name: "ResultCode",
                table: "ProviderExecutionFinalizations");
        }
    }
}
