using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieTakeSelects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SourceSelectId",
                table: "MovieTimelineItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MovieTakeSelects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieTakeId = table.Column<Guid>(type: "uuid", nullable: false),
                    SelectNumber = table.Column<int>(type: "integer", nullable: false),
                    Label = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false, defaultValue: "Draft"),
                    StartMilliseconds = table.Column<int>(type: "integer", nullable: false),
                    EndMilliseconds = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ProvenanceJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewNote = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieTakeSelects", x => x.Id);
                    table.CheckConstraint("CK_MovieTakeSelects_Range", "\"StartMilliseconds\" >= 0 AND \"EndMilliseconds\" > \"StartMilliseconds\"");
                    table.ForeignKey(
                        name: "FK_MovieTakeSelects_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieTakeSelects_AspNetUsers_ReviewedByUserId",
                        column: x => x.ReviewedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieTakeSelects_MovieTakes_MovieTakeId",
                        column: x => x.MovieTakeId,
                        principalTable: "MovieTakes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MovieTimelineItems_SourceSelectId",
                table: "MovieTimelineItems",
                column: "SourceSelectId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieTakeSelects_CreatedByUserId",
                table: "MovieTakeSelects",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieTakeSelects_MovieTakeId_SelectNumber",
                table: "MovieTakeSelects",
                columns: new[] { "MovieTakeId", "SelectNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieTakeSelects_MovieTakeId_Status",
                table: "MovieTakeSelects",
                columns: new[] { "MovieTakeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieTakeSelects_ReviewedByUserId",
                table: "MovieTakeSelects",
                column: "ReviewedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_MovieTimelineItems_MovieTakeSelects_SourceSelectId",
                table: "MovieTimelineItems",
                column: "SourceSelectId",
                principalTable: "MovieTakeSelects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MovieTimelineItems_MovieTakeSelects_SourceSelectId",
                table: "MovieTimelineItems");

            migrationBuilder.DropTable(
                name: "MovieTakeSelects");

            migrationBuilder.DropIndex(
                name: "IX_MovieTimelineItems_SourceSelectId",
                table: "MovieTimelineItems");

            migrationBuilder.DropColumn(
                name: "SourceSelectId",
                table: "MovieTimelineItems");
        }
    }
}
