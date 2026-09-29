using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderBenchmarkingFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProviderBenchmarkRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunKey = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    BenchmarkDefinition = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    FixtureManifestHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    HarnessVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    EnvironmentFingerprint = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: true),
                    ProvenanceJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CompletionCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderBenchmarkRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProviderBenchmarkScenarios",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderBenchmarkRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScenarioKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    FixtureRevision = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CharacteristicsHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ShotType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CameraMotion = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    AspectRatio = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    TargetResolution = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: true),
                    SubjectCount = table.Column<int>(type: "integer", nullable: true),
                    ReferenceFrameCount = table.Column<int>(type: "integer", nullable: true),
                    RequiresCharacterContinuity = table.Column<bool>(type: "boolean", nullable: true),
                    HasDialogue = table.Column<bool>(type: "boolean", nullable: true),
                    HasNativeAudio = table.Column<bool>(type: "boolean", nullable: true),
                    CharacteristicsJson = table.Column<string>(type: "character varying(40000)", maxLength: 40000, nullable: true),
                    FixtureProvenanceJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderBenchmarkScenarios", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProviderBenchmarkScenarios_ProviderBenchmarkRuns_ProviderBe~",
                        column: x => x.ProviderBenchmarkRunId,
                        principalTable: "ProviderBenchmarkRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProviderBenchmarkMeasurements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderBenchmarkRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderBenchmarkScenarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    MeasurementKey = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ModelKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    QueueLatencyMs = table.Column<long>(type: "bigint", nullable: true),
                    TimeToFirstFrameMs = table.Column<long>(type: "bigint", nullable: true),
                    GenerationLatencyMs = table.Column<long>(type: "bigint", nullable: true),
                    TotalLatencyMs = table.Column<long>(type: "bigint", nullable: true),
                    OutputDurationMs = table.Column<int>(type: "integer", nullable: true),
                    OutputWidth = table.Column<int>(type: "integer", nullable: true),
                    OutputHeight = table.Column<int>(type: "integer", nullable: true),
                    OutputBytes = table.Column<long>(type: "bigint", nullable: true),
                    EstimatedCostUsd = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    ActualCostUsd = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    ActualCostKnown = table.Column<bool>(type: "boolean", nullable: false),
                    QualityScore = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    ContinuityScore = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    TemporalStabilityScore = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    PromptAdherenceScore = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    MetricsJson = table.Column<string>(type: "character varying(40000)", maxLength: 40000, nullable: true),
                    ProvenanceJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderBenchmarkMeasurements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProviderBenchmarkMeasurements_ProviderBenchmarkRuns_Provide~",
                        column: x => x.ProviderBenchmarkRunId,
                        principalTable: "ProviderBenchmarkRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProviderBenchmarkMeasurements_ProviderBenchmarkScenarios_Pr~",
                        column: x => x.ProviderBenchmarkScenarioId,
                        principalTable: "ProviderBenchmarkScenarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProviderBenchmarkEvidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderBenchmarkMeasurementId = table.Column<Guid>(type: "uuid", nullable: false),
                    EvidenceKey = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    EvidenceType = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Source = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Reference = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: true),
                    ContentHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    PayloadJson = table.Column<string>(type: "character varying(40000)", maxLength: 40000, nullable: true),
                    ProvenanceJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    CapturedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderBenchmarkEvidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProviderBenchmarkEvidence_ProviderBenchmarkMeasurements_Pro~",
                        column: x => x.ProviderBenchmarkMeasurementId,
                        principalTable: "ProviderBenchmarkMeasurements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderBenchmarkEvidence_EvidenceType_CapturedAt",
                table: "ProviderBenchmarkEvidence",
                columns: new[] { "EvidenceType", "CapturedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderBenchmarkEvidence_ProviderBenchmarkMeasurementId_Ev~",
                table: "ProviderBenchmarkEvidence",
                columns: new[] { "ProviderBenchmarkMeasurementId", "EvidenceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProviderBenchmarkMeasurements_ProviderBenchmarkRunId_Measur~",
                table: "ProviderBenchmarkMeasurements",
                columns: new[] { "ProviderBenchmarkRunId", "MeasurementKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProviderBenchmarkMeasurements_ProviderBenchmarkScenarioId_P~",
                table: "ProviderBenchmarkMeasurements",
                columns: new[] { "ProviderBenchmarkScenarioId", "ProviderKey", "ModelKey" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderBenchmarkMeasurements_ProviderKey_ModelKey_Recorded~",
                table: "ProviderBenchmarkMeasurements",
                columns: new[] { "ProviderKey", "ModelKey", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderBenchmarkRuns_RunKey",
                table: "ProviderBenchmarkRuns",
                column: "RunKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProviderBenchmarkRuns_Status_CreatedAt",
                table: "ProviderBenchmarkRuns",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderBenchmarkScenarios_CharacteristicsHash_ShotType",
                table: "ProviderBenchmarkScenarios",
                columns: new[] { "CharacteristicsHash", "ShotType" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderBenchmarkScenarios_ProviderBenchmarkRunId_ScenarioK~",
                table: "ProviderBenchmarkScenarios",
                columns: new[] { "ProviderBenchmarkRunId", "ScenarioKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderBenchmarkEvidence");

            migrationBuilder.DropTable(
                name: "ProviderBenchmarkMeasurements");

            migrationBuilder.DropTable(
                name: "ProviderBenchmarkScenarios");

            migrationBuilder.DropTable(
                name: "ProviderBenchmarkRuns");
        }
    }
}
