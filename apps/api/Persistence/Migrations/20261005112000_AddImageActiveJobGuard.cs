using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations;

public partial class AddImageActiveJobGuard : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_GenerationJobs_ImageActiveByUser",
            table: "GenerationJobs",
            columns: new[] { "WorkspaceId", "CreatedByUserId", "JobType" },
            unique: true,
            filter: "\"JobType\" = 'image.generate' AND \"RequestId\" LIKE 'image-studio:%' AND \"Status\" IN ('Pending', 'Queued', 'Running')");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_GenerationJobs_ImageActiveByUser",
            table: "GenerationJobs");
    }
}
