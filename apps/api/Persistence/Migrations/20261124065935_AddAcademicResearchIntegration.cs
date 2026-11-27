using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAcademicResearchIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AcademicEvidenceAuditEvents",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventKind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ActorKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: true),
                    PayloadCanonical = table.Column<string>(type: "text", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PreviousHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EntryHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcademicEvidenceAuditEvents", x => new { x.SourceId, x.Sequence });
                });

            migrationBuilder.CreateTable(
                name: "AcademicEvidenceMatrixCells",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuestionKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    QuestionText = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Stance = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EvidenceIdsJson = table.Column<string>(type: "text", nullable: false),
                    HasVerifiableLocator = table.Column<bool>(type: "boolean", nullable: false),
                    AccessLevel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Provenance = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Quality = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Screening = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FlagsJson = table.Column<string>(type: "text", nullable: false),
                    BuiltAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcademicEvidenceMatrixCells", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AcademicEvidenceMatrixFindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    FindingType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    QuestionKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LeftSourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    RightSourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Explanation = table.Column<string>(type: "text", nullable: false),
                    SourceIdsJson = table.Column<string>(type: "text", nullable: false),
                    ReasonCodesJson = table.Column<string>(type: "text", nullable: false),
                    SuggestedStep = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    BuiltAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcademicEvidenceMatrixFindings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AcademicEvidenceSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Subtitle = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    OriginalScriptTitle = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    SourceKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PublicationYear = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    YearBasis = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PublicationDateUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Publisher = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    EditionLabel = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    EditionNumber = table.Column<int>(type: "integer", nullable: true),
                    Volume = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Issue = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    PageRange = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PageRangeOrigin = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Venue = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    AccessLevel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ContentAcquisition = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LandingPageUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Discipline = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    StudyDesign = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CorpusId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AuthorsJson = table.Column<string>(type: "text", nullable: false),
                    EditorsJson = table.Column<string>(type: "text", nullable: false),
                    IdentifiersJson = table.Column<string>(type: "text", nullable: false),
                    AbstractText = table.Column<string>(type: "text", nullable: true),
                    FullText = table.Column<string>(type: "text", nullable: true),
                    ContentSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DedupeKey = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    DuplicateOfSourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProtocolId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    NormalizedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcademicEvidenceSources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AcademicWorkflowProjects",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StateCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    StateDetail = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    SnapshotJson = table.Column<string>(type: "text", nullable: false),
                    EvidenceProjectedRevision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcademicWorkflowProjects", x => x.ProjectId);
                });

            migrationBuilder.CreateTable(
                name: "AcademicEvidenceItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Topic = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SupportKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: true),
                    Claim = table.Column<string>(type: "text", nullable: true),
                    ClaimJson = table.Column<string>(type: "text", nullable: true),
                    LocatorKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    LocatorOrigin = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    LocatorLabel = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LocatorPaginationBasis = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    AnchorStart = table.Column<int>(type: "integer", nullable: true),
                    AnchorEnd = table.Column<int>(type: "integer", nullable: true),
                    LocatorRejectionCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Integrity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IntegrityReasonsJson = table.Column<string>(type: "text", nullable: false),
                    FlagsJson = table.Column<string>(type: "text", nullable: false),
                    AccessLevelAtRecord = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcademicEvidenceItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AcademicEvidenceItems_AcademicEvidenceSources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "AcademicEvidenceSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AcademicEvidenceProvenance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Verdict = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CorroboratingOriginCount = table.Column<int>(type: "integer", nullable: false),
                    ReasonCodesJson = table.Column<string>(type: "text", nullable: false),
                    FieldEvidenceJson = table.Column<string>(type: "text", nullable: false),
                    RetractionNoticesJson = table.Column<string>(type: "text", nullable: false),
                    MayPresentAsVerified = table.Column<bool>(type: "boolean", nullable: false),
                    AssessedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcademicEvidenceProvenance", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AcademicEvidenceProvenance_AcademicEvidenceSources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "AcademicEvidenceSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AcademicEvidenceScreening",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProtocolId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    QualityTier = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    QualityScore = table.Column<double>(type: "double precision", nullable: false),
                    QualityCoverage = table.Column<double>(type: "double precision", nullable: false),
                    CriteriaJson = table.Column<string>(type: "text", nullable: false),
                    FlagsJson = table.Column<string>(type: "text", nullable: false),
                    BiasJson = table.Column<string>(type: "text", nullable: true),
                    Decision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    InclusionReasonsJson = table.Column<string>(type: "text", nullable: false),
                    ExclusionReasonsJson = table.Column<string>(type: "text", nullable: false),
                    UndeterminedReasonsJson = table.Column<string>(type: "text", nullable: false),
                    DecidedBy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProvenanceVerdict = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MemberNote = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    ScreenedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcademicEvidenceScreening", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AcademicEvidenceScreening_AcademicEvidenceSources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "AcademicEvidenceSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AcademicWorkflowCheckpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CheckpointKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    CommittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcademicWorkflowCheckpoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AcademicWorkflowCheckpoints_AcademicWorkflowProjects_Projec~",
                        column: x => x.ProjectId,
                        principalTable: "AcademicWorkflowProjects",
                        principalColumn: "ProjectId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AcademicEvidenceAuditEvents_EntryHash",
                table: "AcademicEvidenceAuditEvents",
                column: "EntryHash");

            migrationBuilder.CreateIndex(
                name: "IX_AcademicEvidenceAuditEvents_WorkspaceId_SourceId_Sequence",
                table: "AcademicEvidenceAuditEvents",
                columns: new[] { "WorkspaceId", "SourceId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_AcademicEvidenceItems_SourceId_Topic",
                table: "AcademicEvidenceItems",
                columns: new[] { "SourceId", "Topic" });

            migrationBuilder.CreateIndex(
                name: "IX_AcademicEvidenceItems_WorkspaceId_Integrity",
                table: "AcademicEvidenceItems",
                columns: new[] { "WorkspaceId", "Integrity" });

            migrationBuilder.CreateIndex(
                name: "IX_AcademicEvidenceMatrixCells_ProjectId_QuestionKey_SourceId",
                table: "AcademicEvidenceMatrixCells",
                columns: new[] { "ProjectId", "QuestionKey", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AcademicEvidenceMatrixCells_WorkspaceId_ProjectId",
                table: "AcademicEvidenceMatrixCells",
                columns: new[] { "WorkspaceId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_AcademicEvidenceMatrixFindings_ProjectId_FindingType_Code",
                table: "AcademicEvidenceMatrixFindings",
                columns: new[] { "ProjectId", "FindingType", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_AcademicEvidenceMatrixFindings_WorkspaceId_ProjectId",
                table: "AcademicEvidenceMatrixFindings",
                columns: new[] { "WorkspaceId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_AcademicEvidenceProvenance_SourceId",
                table: "AcademicEvidenceProvenance",
                column: "SourceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AcademicEvidenceProvenance_WorkspaceId_Verdict",
                table: "AcademicEvidenceProvenance",
                columns: new[] { "WorkspaceId", "Verdict" });

            migrationBuilder.CreateIndex(
                name: "IX_AcademicEvidenceScreening_SourceId_ProtocolId",
                table: "AcademicEvidenceScreening",
                columns: new[] { "SourceId", "ProtocolId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AcademicEvidenceScreening_WorkspaceId_Decision",
                table: "AcademicEvidenceScreening",
                columns: new[] { "WorkspaceId", "Decision" });

            migrationBuilder.CreateIndex(
                name: "IX_AcademicEvidenceSources_WorkspaceId",
                table: "AcademicEvidenceSources",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_AcademicEvidenceSources_WorkspaceId_CorpusId",
                table: "AcademicEvidenceSources",
                columns: new[] { "WorkspaceId", "CorpusId" });

            migrationBuilder.CreateIndex(
                name: "IX_AcademicEvidenceSources_WorkspaceId_ProjectId",
                table: "AcademicEvidenceSources",
                columns: new[] { "WorkspaceId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_AcademicEvidenceSources_WorkspaceId_ProjectId_DedupeKey",
                table: "AcademicEvidenceSources",
                columns: new[] { "WorkspaceId", "ProjectId", "DedupeKey" },
                unique: true,
                filter: "\"DuplicateOfSourceId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AcademicWorkflowCheckpoints_CheckpointKey",
                table: "AcademicWorkflowCheckpoints",
                column: "CheckpointKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AcademicWorkflowCheckpoints_ProjectId_Revision",
                table: "AcademicWorkflowCheckpoints",
                columns: new[] { "ProjectId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AcademicWorkflowCheckpoints_WorkspaceId",
                table: "AcademicWorkflowCheckpoints",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_AcademicWorkflowProjects_WorkspaceId_CreatedAtUtc",
                table: "AcademicWorkflowProjects",
                columns: new[] { "WorkspaceId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AcademicWorkflowProjects_WorkspaceId_RequestKey",
                table: "AcademicWorkflowProjects",
                columns: new[] { "WorkspaceId", "RequestKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AcademicWorkflowProjects_WorkspaceId_State",
                table: "AcademicWorkflowProjects",
                columns: new[] { "WorkspaceId", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AcademicEvidenceAuditEvents");

            migrationBuilder.DropTable(
                name: "AcademicEvidenceItems");

            migrationBuilder.DropTable(
                name: "AcademicEvidenceMatrixCells");

            migrationBuilder.DropTable(
                name: "AcademicEvidenceMatrixFindings");

            migrationBuilder.DropTable(
                name: "AcademicEvidenceProvenance");

            migrationBuilder.DropTable(
                name: "AcademicEvidenceScreening");

            migrationBuilder.DropTable(
                name: "AcademicWorkflowCheckpoints");

            migrationBuilder.DropTable(
                name: "AcademicEvidenceSources");

            migrationBuilder.DropTable(
                name: "AcademicWorkflowProjects");
        }
    }
}
