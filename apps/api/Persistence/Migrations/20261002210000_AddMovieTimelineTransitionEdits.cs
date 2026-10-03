using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Taslim.Api.Persistence;

#nullable disable

namespace Taslim.Api.Persistence.Migrations;

[DbContext(typeof(TaslimDbContext))]
[Migration("20261002210000_AddMovieTimelineTransitionEdits")]
public partial class AddMovieTimelineTransitionEdits : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "MovieTimelineTransitionEdits",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                MovieTimelineId = table.Column<Guid>(type: "uuid", nullable: false),
                MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                DecisionId = table.Column<Guid>(type: "uuid", nullable: false),
                BaseTimelineVersion = table.Column<int>(type: "integer", nullable: false),
                ResultTimelineVersion = table.Column<int>(type: "integer", nullable: false),
                Action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                DecisionJson = table.Column<string>(type: "character varying(40000)", maxLength: 40000, nullable: false),
                ResultTimelineJson = table.Column<string>(type: "character varying(100000)", maxLength: 100000, nullable: false),
                CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MovieTimelineTransitionEdits", x => x.Id);
                table.ForeignKey("FK_MovieTimelineTransitionEdits_AspNetUsers_CreatedByUserId", x => x.CreatedByUserId, "AspNetUsers", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_MovieTimelineTransitionEdits_MovieProjects_MovieProjectId", x => x.MovieProjectId, "MovieProjects", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_MovieTimelineTransitionEdits_MovieTimelines_MovieTimelineId", x => x.MovieTimelineId, "MovieTimelines", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_MovieTimelineTransitionEdits_CreatedByUserId",
            table: "MovieTimelineTransitionEdits",
            column: "CreatedByUserId");
        migrationBuilder.CreateIndex(
            name: "IX_MovieTimelineTransitionEdits_MovieProjectId_ResultTimelineVersion",
            table: "MovieTimelineTransitionEdits",
            columns: new[] { "MovieProjectId", "ResultTimelineVersion" });
        migrationBuilder.CreateIndex(
            name: "IX_MovieTimelineTransitionEdits_MovieTimelineId_DecisionId",
            table: "MovieTimelineTransitionEdits",
            columns: new[] { "MovieTimelineId", "DecisionId" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_MovieTimelineTransitionEdits_MovieProjectId",
            table: "MovieTimelineTransitionEdits",
            column: "MovieProjectId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "MovieTimelineTransitionEdits");
    }
}
