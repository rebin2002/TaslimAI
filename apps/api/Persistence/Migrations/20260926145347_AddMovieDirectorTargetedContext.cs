using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieDirectorTargetedContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DirectorProjectContexts_MovieProjectId",
                table: "DirectorProjectContexts");

            migrationBuilder.AddColumn<Guid>(
                name: "TargetId",
                table: "DirectorProjectContexts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "TargetType",
                table: "DirectorProjectContexts",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("UPDATE \"DirectorProjectContexts\" SET \"TargetType\" = 'project', \"TargetId\" = \"MovieProjectId\";");

            migrationBuilder.CreateIndex(
                name: "IX_DirectorProjectContexts_MovieProjectId_TargetType_TargetId",
                table: "DirectorProjectContexts",
                columns: new[] { "MovieProjectId", "TargetType", "TargetId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DirectorProjectContexts_MovieProjectId_TargetType_TargetId",
                table: "DirectorProjectContexts");

            migrationBuilder.DropColumn(
                name: "TargetId",
                table: "DirectorProjectContexts");

            migrationBuilder.DropColumn(
                name: "TargetType",
                table: "DirectorProjectContexts");

            migrationBuilder.CreateIndex(
                name: "IX_DirectorProjectContexts_MovieProjectId",
                table: "DirectorProjectContexts",
                column: "MovieProjectId",
                unique: true);
        }
    }
}
