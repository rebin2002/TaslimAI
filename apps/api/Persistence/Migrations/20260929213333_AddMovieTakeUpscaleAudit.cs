using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieTakeUpscaleAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MovieTakeUpscaleAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieTakeId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetMasterResolution = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SourceResolution = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    EligibilityCode = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    WasSelected = table.Column<bool>(type: "boolean", nullable: false),
                    WasFinal = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    GenerationJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    OutcomeNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieTakeUpscaleAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieTakeUpscaleAudits_AspNetUsers_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieTakeUpscaleAudits_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MovieTakeUpscaleAudits_MovieTakes_MovieTakeId",
                        column: x => x.MovieTakeId,
                        principalTable: "MovieTakes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MovieTakeUpscaleAudits_MovieProjectId_CreatedAt",
                table: "MovieTakeUpscaleAudits",
                columns: new[] { "MovieProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieTakeUpscaleAudits_MovieTakeId_CreatedAt",
                table: "MovieTakeUpscaleAudits",
                columns: new[] { "MovieTakeId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieTakeUpscaleAudits_RequestedByUserId",
                table: "MovieTakeUpscaleAudits",
                column: "RequestedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MovieTakeUpscaleAudits");
        }
    }
}
