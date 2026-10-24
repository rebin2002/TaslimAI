using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceMovieFinalMasterActiveRequest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MovieFinalMasters_SourceTakeId_TargetProfile_SupersededByMa~",
                table: "MovieFinalMasters");

            migrationBuilder.CreateIndex(
                name: "IX_MovieFinalMasters_SourceTakeId_TargetProfile_SupersededByMa~",
                table: "MovieFinalMasters",
                columns: new[] { "SourceTakeId", "TargetProfile", "SupersededByMasterId" },
                unique: true,
                filter: "\"SupersededByMasterId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MovieFinalMasters_SourceTakeId_TargetProfile_SupersededByMa~",
                table: "MovieFinalMasters");

            migrationBuilder.CreateIndex(
                name: "IX_MovieFinalMasters_SourceTakeId_TargetProfile_SupersededByMa~",
                table: "MovieFinalMasters",
                columns: new[] { "SourceTakeId", "TargetProfile", "SupersededByMasterId" });
        }
    }
}
