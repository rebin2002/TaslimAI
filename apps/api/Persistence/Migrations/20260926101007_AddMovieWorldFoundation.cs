using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieWorldFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MovieContinuityFacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopeType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: true),
                    FactKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    FactValue = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieContinuityFacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieContinuityFacts_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieContinuityLocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    FieldName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    LockedValue = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Strength = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReleasedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieContinuityLocks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieContinuityLocks_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieContinuityLocks_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieProps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Category = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ContinuityNotes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ReferenceAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieProps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieProps_Assets_ReferenceAssetId",
                        column: x => x.ReferenceAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieProps_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieSets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieLocationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    EnvironmentType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    VisualDescription = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    TimeOfDay = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Weather = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    ContinuityNotes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ReferenceAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieSets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieSets_Assets_ReferenceAssetId",
                        column: x => x.ReferenceAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieSets_MovieLocations_MovieLocationId",
                        column: x => x.MovieLocationId,
                        principalTable: "MovieLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieSets_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieWorldReferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    TagsJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieWorldReferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieWorldReferences_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieWorldReferences_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieWorldUsages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieSceneId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieShotId = table.Column<Guid>(type: "uuid", nullable: true),
                    EntityType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieWorldUsages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieWorldUsages_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MovieWorldUsages_MovieScenes_MovieSceneId",
                        column: x => x.MovieSceneId,
                        principalTable: "MovieScenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MovieWorldUsages_MovieShots_MovieShotId",
                        column: x => x.MovieShotId,
                        principalTable: "MovieShots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MovieWorldReferenceLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieWorldReferenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieWorldReferenceLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieWorldReferenceLinks_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MovieWorldReferenceLinks_MovieWorldReferences_MovieWorldRef~",
                        column: x => x.MovieWorldReferenceId,
                        principalTable: "MovieWorldReferences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MovieContinuityFacts_MovieProjectId_ScopeType_ScopeId_FactK~",
                table: "MovieContinuityFacts",
                columns: new[] { "MovieProjectId", "ScopeType", "ScopeId", "FactKey" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieContinuityLocks_CreatedByUserId",
                table: "MovieContinuityLocks",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieContinuityLocks_MovieProjectId_EntityType_EntityId_Fie~",
                table: "MovieContinuityLocks",
                columns: new[] { "MovieProjectId", "EntityType", "EntityId", "FieldName", "ReleasedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieProps_MovieProjectId",
                table: "MovieProps",
                column: "MovieProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProps_ReferenceAssetId",
                table: "MovieProps",
                column: "ReferenceAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieSets_MovieLocationId",
                table: "MovieSets",
                column: "MovieLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieSets_MovieProjectId",
                table: "MovieSets",
                column: "MovieProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieSets_ReferenceAssetId",
                table: "MovieSets",
                column: "ReferenceAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieWorldReferenceLinks_MovieProjectId_EntityType_EntityId",
                table: "MovieWorldReferenceLinks",
                columns: new[] { "MovieProjectId", "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieWorldReferenceLinks_MovieWorldReferenceId_EntityType_E~",
                table: "MovieWorldReferenceLinks",
                columns: new[] { "MovieWorldReferenceId", "EntityType", "EntityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieWorldReferences_AssetId",
                table: "MovieWorldReferences",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieWorldReferences_MovieProjectId_Kind",
                table: "MovieWorldReferences",
                columns: new[] { "MovieProjectId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieWorldUsages_MovieProjectId_EntityType_EntityId",
                table: "MovieWorldUsages",
                columns: new[] { "MovieProjectId", "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieWorldUsages_MovieSceneId_MovieShotId_EntityType_Entity~",
                table: "MovieWorldUsages",
                columns: new[] { "MovieSceneId", "MovieShotId", "EntityType", "EntityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieWorldUsages_MovieShotId",
                table: "MovieWorldUsages",
                column: "MovieShotId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MovieContinuityFacts");

            migrationBuilder.DropTable(
                name: "MovieContinuityLocks");

            migrationBuilder.DropTable(
                name: "MovieProps");

            migrationBuilder.DropTable(
                name: "MovieSets");

            migrationBuilder.DropTable(
                name: "MovieWorldReferenceLinks");

            migrationBuilder.DropTable(
                name: "MovieWorldUsages");

            migrationBuilder.DropTable(
                name: "MovieWorldReferences");
        }
    }
}
