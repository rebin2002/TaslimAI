using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieProductionComplexity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MovieProductionComplexityAssessments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieShotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ProfileJson = table.Column<string>(type: "character varying(30000)", maxLength: 30000, nullable: false),
                    OverallScore = table.Column<int>(type: "integer", nullable: false),
                    OverallBand = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieProductionComplexityAssessments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieProductionComplexityAssessments_MovieShots_MovieShotId",
                        column: x => x.MovieShotId,
                        principalTable: "MovieShots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionComplexityAssessments_MovieShotId_CreatedAt",
                table: "MovieProductionComplexityAssessments",
                columns: new[] { "MovieShotId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionComplexityAssessments_MovieShotId_Version",
                table: "MovieProductionComplexityAssessments",
                columns: new[] { "MovieShotId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MovieProductionComplexityAssessments");
        }
    }
}
