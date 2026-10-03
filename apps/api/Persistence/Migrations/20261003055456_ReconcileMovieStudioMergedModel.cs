using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable
namespace Taslim.Api.Persistence.Migrations;

/// <summary>
/// Reconciles EF's model snapshot after the manually authored transition-edit migration.
/// The table is created by 20261002210000_AddMovieTimelineTransitionEdits; this migration
/// intentionally performs no schema operation and only establishes the complete snapshot.
/// </summary>
public partial class ReconcileMovieStudioMergedModel : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) { }

    protected override void Down(MigrationBuilder migrationBuilder) { }
}
