using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieFinalAssemblyExport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "MovieAssemblies",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "AudioMixJson",
                table: "MovieAssemblies",
                type: "character varying(40000)",
                maxLength: 40000,
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "CaptionsJson",
                table: "MovieAssemblies",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "CheckpointJson",
                table: "MovieAssemblies",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "MovieAssemblies",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastErrorCode",
                table: "MovieAssemblies",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OutputHeight",
                table: "MovieAssemblies",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "OutputWidth",
                table: "MovieAssemblies",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ProgressPercent",
                table: "MovieAssemblies",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ProvenanceJson",
                table: "MovieAssemblies",
                type: "character varying(20000)",
                maxLength: 20000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QcResultJson",
                table: "MovieAssemblies",
                type: "character varying(20000)",
                maxLength: 20000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QcStatus",
                table: "MovieAssemblies",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "NotRun");

            migrationBuilder.AddColumn<string>(
                name: "RequestFingerprint",
                table: "MovieAssemblies",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RequestedByUserId",
                table: "MovieAssemblies",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolutionProfile",
                table: "MovieAssemblies",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "uhd-4k");

            migrationBuilder.AddColumn<string>(
                name: "TimelineJson",
                table: "MovieAssemblies",
                type: "character varying(100000)",
                maxLength: 100000,
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "MovieAssemblies",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "CURRENT_TIMESTAMP");

            migrationBuilder.CreateIndex(
                name: "IX_MovieAssemblies_MovieProjectId_IdempotencyKey",
                table: "MovieAssemblies",
                columns: new[] { "MovieProjectId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieAssemblies_RequestedByUserId",
                table: "MovieAssemblies",
                column: "RequestedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_MovieAssemblies_AspNetUsers_RequestedByUserId",
                table: "MovieAssemblies",
                column: "RequestedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MovieAssemblies_AspNetUsers_RequestedByUserId",
                table: "MovieAssemblies");

            migrationBuilder.DropIndex(
                name: "IX_MovieAssemblies_MovieProjectId_IdempotencyKey",
                table: "MovieAssemblies");

            migrationBuilder.DropIndex(
                name: "IX_MovieAssemblies_RequestedByUserId",
                table: "MovieAssemblies");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "MovieAssemblies");

            migrationBuilder.DropColumn(
                name: "AudioMixJson",
                table: "MovieAssemblies");

            migrationBuilder.DropColumn(
                name: "CaptionsJson",
                table: "MovieAssemblies");

            migrationBuilder.DropColumn(
                name: "CheckpointJson",
                table: "MovieAssemblies");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "MovieAssemblies");

            migrationBuilder.DropColumn(
                name: "LastErrorCode",
                table: "MovieAssemblies");

            migrationBuilder.DropColumn(
                name: "OutputHeight",
                table: "MovieAssemblies");

            migrationBuilder.DropColumn(
                name: "OutputWidth",
                table: "MovieAssemblies");

            migrationBuilder.DropColumn(
                name: "ProgressPercent",
                table: "MovieAssemblies");

            migrationBuilder.DropColumn(
                name: "ProvenanceJson",
                table: "MovieAssemblies");

            migrationBuilder.DropColumn(
                name: "QcResultJson",
                table: "MovieAssemblies");

            migrationBuilder.DropColumn(
                name: "QcStatus",
                table: "MovieAssemblies");

            migrationBuilder.DropColumn(
                name: "RequestFingerprint",
                table: "MovieAssemblies");

            migrationBuilder.DropColumn(
                name: "RequestedByUserId",
                table: "MovieAssemblies");

            migrationBuilder.DropColumn(
                name: "ResolutionProfile",
                table: "MovieAssemblies");

            migrationBuilder.DropColumn(
                name: "TimelineJson",
                table: "MovieAssemblies");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "MovieAssemblies");
        }
    }
}
