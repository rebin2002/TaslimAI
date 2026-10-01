using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieKeyframeSelectionAndLocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SelectedKeyframeVersionId",
                table: "MovieShots",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsLocked",
                table: "MovieProductionVersions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LockedAt",
                table: "MovieProductionVersions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LockedByUserId",
                table: "MovieProductionVersions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieShots_SelectedKeyframeVersionId",
                table: "MovieShots",
                column: "SelectedKeyframeVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionVersions_LockedByUserId",
                table: "MovieProductionVersions",
                column: "LockedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_MovieProductionVersions_AspNetUsers_LockedByUserId",
                table: "MovieProductionVersions",
                column: "LockedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MovieShots_MovieProductionVersions_SelectedKeyframeVersionId",
                table: "MovieShots",
                column: "SelectedKeyframeVersionId",
                principalTable: "MovieProductionVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MovieProductionVersions_AspNetUsers_LockedByUserId",
                table: "MovieProductionVersions");

            migrationBuilder.DropForeignKey(
                name: "FK_MovieShots_MovieProductionVersions_SelectedKeyframeVersionId",
                table: "MovieShots");

            migrationBuilder.DropIndex(
                name: "IX_MovieShots_SelectedKeyframeVersionId",
                table: "MovieShots");

            migrationBuilder.DropIndex(
                name: "IX_MovieProductionVersions_LockedByUserId",
                table: "MovieProductionVersions");

            migrationBuilder.DropColumn(
                name: "SelectedKeyframeVersionId",
                table: "MovieShots");

            migrationBuilder.DropColumn(
                name: "IsLocked",
                table: "MovieProductionVersions");

            migrationBuilder.DropColumn(
                name: "LockedAt",
                table: "MovieProductionVersions");

            migrationBuilder.DropColumn(
                name: "LockedByUserId",
                table: "MovieProductionVersions");
        }
    }
}
