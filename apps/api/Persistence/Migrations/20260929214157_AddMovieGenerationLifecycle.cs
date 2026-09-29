using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieGenerationLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "MovieTakes",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Planned",
                oldClrType: typeof(string),
                oldType: "character varying(30)",
                oldMaxLength: 30,
                oldDefaultValue: "Draft");

            migrationBuilder.AddColumn<Guid>(
                name: "RetryOfTakeId",
                table: "MovieTakes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RetryOfJobId",
                table: "GenerationJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieTakes_RetryOfTakeId",
                table: "MovieTakes",
                column: "RetryOfTakeId");

            migrationBuilder.CreateIndex(
                name: "IX_GenerationJobs_RetryOfJobId",
                table: "GenerationJobs",
                column: "RetryOfJobId");

            migrationBuilder.AddForeignKey(
                name: "FK_GenerationJobs_GenerationJobs_RetryOfJobId",
                table: "GenerationJobs",
                column: "RetryOfJobId",
                principalTable: "GenerationJobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MovieTakes_MovieTakes_RetryOfTakeId",
                table: "MovieTakes",
                column: "RetryOfTakeId",
                principalTable: "MovieTakes",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_GenerationJobs_GenerationJobs_RetryOfJobId",
                table: "GenerationJobs");

            migrationBuilder.DropForeignKey(
                name: "FK_MovieTakes_MovieTakes_RetryOfTakeId",
                table: "MovieTakes");

            migrationBuilder.DropIndex(
                name: "IX_MovieTakes_RetryOfTakeId",
                table: "MovieTakes");

            migrationBuilder.DropIndex(
                name: "IX_GenerationJobs_RetryOfJobId",
                table: "GenerationJobs");

            migrationBuilder.DropColumn(
                name: "RetryOfTakeId",
                table: "MovieTakes");

            migrationBuilder.DropColumn(
                name: "RetryOfJobId",
                table: "GenerationJobs");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "MovieTakes",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Draft",
                oldClrType: typeof(string),
                oldType: "character varying(30)",
                oldMaxLength: 30,
                oldDefaultValue: "Planned");
        }
    }
}
