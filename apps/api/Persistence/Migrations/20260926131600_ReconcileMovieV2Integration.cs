using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReconcileMovieV2Integration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProductionStage",
                table: "MovieShots",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

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
                name: "MovieProductionVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieShotId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Stage = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Label = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    CompositionJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    RegenerationMetadataJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    StageProvenanceJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    SourceVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    GenerationJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    FirstFrameAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastFrameAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    FirstFrameNotes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LastFrameNotes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieProductionVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieProductionVersions_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieProductionVersions_AspNetUsers_ReviewedByUserId",
                        column: x => x.ReviewedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieProductionVersions_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieProductionVersions_Assets_FirstFrameAssetId",
                        column: x => x.FirstFrameAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieProductionVersions_Assets_LastFrameAssetId",
                        column: x => x.LastFrameAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieProductionVersions_GenerationJobs_GenerationJobId",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieProductionVersions_MovieProductionVersions_SourceVersi~",
                        column: x => x.SourceVersionId,
                        principalTable: "MovieProductionVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieProductionVersions_MovieShots_MovieShotId",
                        column: x => x.MovieShotId,
                        principalTable: "MovieShots",
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
                name: "MovieStories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Premise = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Logline = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Synopsis = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    Treatment = table.Column<string>(type: "character varying(40000)", maxLength: 40000, nullable: false),
                    ApprovalState = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CurrentRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieStories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieStories_MovieProjects_MovieProjectId",
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
                name: "MovieProductionStageTransitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieShotId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProductionVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromStage = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ToStage = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    EventType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    MetadataJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    SourceVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    GenerationJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieProductionStageTransitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieProductionStageTransitions_AspNetUsers_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieProductionStageTransitions_GenerationJobs_GenerationJo~",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieProductionStageTransitions_MovieProductionVersions_Mov~",
                        column: x => x.MovieProductionVersionId,
                        principalTable: "MovieProductionVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MovieProductionStageTransitions_MovieProductionVersions_Sou~",
                        column: x => x.SourceVersionId,
                        principalTable: "MovieProductionVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieProductionStageTransitions_MovieShots_MovieShotId",
                        column: x => x.MovieShotId,
                        principalTable: "MovieShots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieProductionVersionAssets",
                columns: table => new
                {
                    MovieProductionVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieProductionVersionAssets", x => new { x.MovieProductionVersionId, x.AssetId, x.Role });
                    table.ForeignKey(
                        name: "FK_MovieProductionVersionAssets_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieProductionVersionAssets_MovieProductionVersions_MovieP~",
                        column: x => x.MovieProductionVersionId,
                        principalTable: "MovieProductionVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieSetVariations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieSetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    VisualDescription = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    TimeOfDay = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Weather = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    Lighting = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ContinuityNotes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ReferenceAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieSetVariations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieSetVariations_Assets_ReferenceAssetId",
                        column: x => x.ReferenceAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieSetVariations_MovieSets_MovieSetId",
                        column: x => x.MovieSetId,
                        principalTable: "MovieSets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieStoryRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieStoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionNumber = table.Column<int>(type: "integer", nullable: false),
                    Premise = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Logline = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Synopsis = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    Treatment = table.Column<string>(type: "character varying(40000)", maxLength: 40000, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Authorship = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ChangeSummary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieStoryRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieStoryRevisions_MovieStories_MovieStoryId",
                        column: x => x.MovieStoryId,
                        principalTable: "MovieStories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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

            migrationBuilder.CreateTable(
                name: "MovieScreenplayScenes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieStoryRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieSceneId = table.Column<Guid>(type: "uuid", nullable: true),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    SceneIdentifier = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ActNumber = table.Column<int>(type: "integer", nullable: true),
                    SequenceNumber = table.Column<int>(type: "integer", nullable: true),
                    Slugline = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Synopsis = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieScreenplayScenes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieScreenplayScenes_MovieScenes_MovieSceneId",
                        column: x => x.MovieSceneId,
                        principalTable: "MovieScenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieScreenplayScenes_MovieStoryRevisions_MovieStoryRevisio~",
                        column: x => x.MovieStoryRevisionId,
                        principalTable: "MovieStoryRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieScreenplayElements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieScreenplaySceneId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    ElementType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Content = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    CharacterName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    Parenthetical = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieScreenplayElements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieScreenplayElements_MovieScreenplayScenes_MovieScreenpl~",
                        column: x => x.MovieScreenplaySceneId,
                        principalTable: "MovieScreenplayScenes",
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
                name: "IX_MovieProductionStageTransitions_ActorUserId",
                table: "MovieProductionStageTransitions",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionStageTransitions_GenerationJobId",
                table: "MovieProductionStageTransitions",
                column: "GenerationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionStageTransitions_MovieProductionVersionId",
                table: "MovieProductionStageTransitions",
                column: "MovieProductionVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionStageTransitions_MovieShotId_CreatedAt",
                table: "MovieProductionStageTransitions",
                columns: new[] { "MovieShotId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionStageTransitions_SourceVersionId",
                table: "MovieProductionStageTransitions",
                column: "SourceVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionVersionAssets_AssetId",
                table: "MovieProductionVersionAssets",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionVersions_AssetId",
                table: "MovieProductionVersions",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionVersions_CreatedByUserId",
                table: "MovieProductionVersions",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionVersions_FirstFrameAssetId",
                table: "MovieProductionVersions",
                column: "FirstFrameAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionVersions_GenerationJobId",
                table: "MovieProductionVersions",
                column: "GenerationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionVersions_LastFrameAssetId",
                table: "MovieProductionVersions",
                column: "LastFrameAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionVersions_MovieShotId_Stage_Status",
                table: "MovieProductionVersions",
                columns: new[] { "MovieShotId", "Stage", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionVersions_MovieShotId_VersionNumber",
                table: "MovieProductionVersions",
                columns: new[] { "MovieShotId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionVersions_ReviewedByUserId",
                table: "MovieProductionVersions",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProductionVersions_SourceVersionId",
                table: "MovieProductionVersions",
                column: "SourceVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProps_MovieProjectId",
                table: "MovieProps",
                column: "MovieProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProps_ReferenceAssetId",
                table: "MovieProps",
                column: "ReferenceAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieScreenplayElements_MovieScreenplaySceneId_Ordinal",
                table: "MovieScreenplayElements",
                columns: new[] { "MovieScreenplaySceneId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieScreenplayScenes_MovieSceneId",
                table: "MovieScreenplayScenes",
                column: "MovieSceneId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieScreenplayScenes_MovieStoryRevisionId_Ordinal",
                table: "MovieScreenplayScenes",
                columns: new[] { "MovieStoryRevisionId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieScreenplayScenes_MovieStoryRevisionId_SceneIdentifier",
                table: "MovieScreenplayScenes",
                columns: new[] { "MovieStoryRevisionId", "SceneIdentifier" },
                unique: true);

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
                name: "IX_MovieSetVariations_MovieSetId_IsDefault",
                table: "MovieSetVariations",
                columns: new[] { "MovieSetId", "IsDefault" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieSetVariations_MovieSetId_Name",
                table: "MovieSetVariations",
                columns: new[] { "MovieSetId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieSetVariations_ReferenceAssetId",
                table: "MovieSetVariations",
                column: "ReferenceAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieStories_MovieProjectId",
                table: "MovieStories",
                column: "MovieProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieStories_WorkspaceId_UpdatedAt",
                table: "MovieStories",
                columns: new[] { "WorkspaceId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieStoryRevisions_MovieStoryId_RevisionNumber",
                table: "MovieStoryRevisions",
                columns: new[] { "MovieStoryId", "RevisionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieStoryRevisions_MovieStoryId_Status",
                table: "MovieStoryRevisions",
                columns: new[] { "MovieStoryId", "Status" });

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
                name: "MovieProductionStageTransitions");

            migrationBuilder.DropTable(
                name: "MovieProductionVersionAssets");

            migrationBuilder.DropTable(
                name: "MovieProps");

            migrationBuilder.DropTable(
                name: "MovieScreenplayElements");

            migrationBuilder.DropTable(
                name: "MovieSetVariations");

            migrationBuilder.DropTable(
                name: "MovieWorldReferenceLinks");

            migrationBuilder.DropTable(
                name: "MovieWorldUsages");

            migrationBuilder.DropTable(
                name: "MovieProductionVersions");

            migrationBuilder.DropTable(
                name: "MovieScreenplayScenes");

            migrationBuilder.DropTable(
                name: "MovieSets");

            migrationBuilder.DropTable(
                name: "MovieWorldReferences");

            migrationBuilder.DropTable(
                name: "MovieStoryRevisions");

            migrationBuilder.DropTable(
                name: "MovieStories");

            migrationBuilder.DropColumn(
                name: "ProductionStage",
                table: "MovieShots");
        }
    }
}
