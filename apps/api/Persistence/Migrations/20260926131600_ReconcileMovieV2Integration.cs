using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations;

/// <summary>
/// Historical reconciliation marker. The approved Movie V2 foundation migrations
/// already create these objects in their upgrade order; keeping this migration as
/// a no-op preserves the published migration identity without replaying DDL.
/// </summary>
public partial class ReconcileMovieV2Integration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
