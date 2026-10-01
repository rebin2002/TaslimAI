using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieDialogueVoiceFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MovieDialogueLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieClipId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieCharacterId = table.Column<Guid>(type: "uuid", nullable: true),
                    SelectedTakeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    SpeakerName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Text = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    StartMilliseconds = table.Column<int>(type: "integer", nullable: false),
                    EndMilliseconds = table.Column<int>(type: "integer", nullable: false),
                    DeliveryNotes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false, defaultValue: "Draft"),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieDialogueLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieDialogueLines_MovieCharacters_MovieCharacterId",
                        column: x => x.MovieCharacterId,
                        principalTable: "MovieCharacters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieDialogueLines_MovieClips_MovieClipId",
                        column: x => x.MovieClipId,
                        principalTable: "MovieClips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieDialogueTakes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieDialogueLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieClipId = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    StoredFileId = table.Column<Guid>(type: "uuid", nullable: true),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Label = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false, defaultValue: "Planned"),
                    DurationMilliseconds = table.Column<int>(type: "integer", nullable: true),
                    MetadataJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    UsageMetadataJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SelectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SelectedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieDialogueTakes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieDialogueTakes_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieDialogueTakes_GenerationJobs_GenerationJobId",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieDialogueTakes_MovieClips_MovieClipId",
                        column: x => x.MovieClipId,
                        principalTable: "MovieClips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MovieDialogueTakes_MovieDialogueLines_MovieDialogueLineId",
                        column: x => x.MovieDialogueLineId,
                        principalTable: "MovieDialogueLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MovieDialogueTakes_StoredFiles_StoredFileId",
                        column: x => x.StoredFileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MovieDialogueTakeApprovals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieDialogueTakeId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Decision = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Comment = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieDialogueTakeApprovals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieDialogueTakeApprovals_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieDialogueTakeApprovals_MovieDialogueTakes_MovieDialogue~",
                        column: x => x.MovieDialogueTakeId,
                        principalTable: "MovieDialogueTakes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MovieDialogueLines_MovieCharacterId",
                table: "MovieDialogueLines",
                column: "MovieCharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieDialogueLines_MovieClipId_Sequence",
                table: "MovieDialogueLines",
                columns: new[] { "MovieClipId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieDialogueLines_MovieClipId_Status",
                table: "MovieDialogueLines",
                columns: new[] { "MovieClipId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieDialogueLines_SelectedTakeId",
                table: "MovieDialogueLines",
                column: "SelectedTakeId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieDialogueTakeApprovals_MovieDialogueTakeId_CreatedAt",
                table: "MovieDialogueTakeApprovals",
                columns: new[] { "MovieDialogueTakeId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieDialogueTakeApprovals_UserId",
                table: "MovieDialogueTakeApprovals",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieDialogueTakes_AssetId",
                table: "MovieDialogueTakes",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieDialogueTakes_GenerationJobId",
                table: "MovieDialogueTakes",
                column: "GenerationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieDialogueTakes_MovieClipId_Status",
                table: "MovieDialogueTakes",
                columns: new[] { "MovieClipId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieDialogueTakes_MovieDialogueLineId_VersionNumber",
                table: "MovieDialogueTakes",
                columns: new[] { "MovieDialogueLineId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieDialogueTakes_StoredFileId",
                table: "MovieDialogueTakes",
                column: "StoredFileId");

            migrationBuilder.AddForeignKey(
                name: "FK_MovieDialogueLines_MovieDialogueTakes_SelectedTakeId",
                table: "MovieDialogueLines",
                column: "SelectedTakeId",
                principalTable: "MovieDialogueTakes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MovieDialogueLines_MovieDialogueTakes_SelectedTakeId",
                table: "MovieDialogueLines");

            migrationBuilder.DropTable(
                name: "MovieDialogueTakeApprovals");

            migrationBuilder.DropTable(
                name: "MovieDialogueTakes");

            migrationBuilder.DropTable(
                name: "MovieDialogueLines");
        }
    }
}
