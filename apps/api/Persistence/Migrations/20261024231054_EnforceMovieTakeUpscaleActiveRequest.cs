using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceMovieTakeUpscaleActiveRequest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "UX_MovieTakeUpscaleAudit_Active",
                table: "MovieTakeUpscaleAudits",
                columns: new[] { "MovieTakeId", "TargetMasterResolution" },
                unique: true,
                filter: "\"Status\" IN ('PendingExecution', 'Completed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_MovieTakeUpscaleAudit_Active",
                table: "MovieTakeUpscaleAudits");
        }
    }
}
