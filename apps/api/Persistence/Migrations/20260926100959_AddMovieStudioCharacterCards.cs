using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieStudioCharacterCards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "ContinuitySnapshotJson",
                table: "MovieClips",
                type: "character varying(100000)",
                maxLength: 100000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(20000)",
                oldMaxLength: 20000,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PersonalityAndStoryNotes",
                table: "MovieCharacters",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhysicalDescription",
                table: "MovieCharacters",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Role",
                table: "MovieCharacters",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VoiceReference",
                table: "MovieCharacters",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Wardrobe",
                table: "MovieCharacters",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MovieCharacterReferenceAssets",
                columns: table => new
                {
                    MovieCharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieCharacterReferenceAssets", x => new { x.MovieCharacterId, x.AssetId });
                    table.ForeignKey(
                        name: "FK_MovieCharacterReferenceAssets_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieCharacterReferenceAssets_MovieCharacters_MovieCharacte~",
                        column: x => x.MovieCharacterId,
                        principalTable: "MovieCharacters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieCharacterRelationships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieCharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelatedCharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelationshipType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieCharacterRelationships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieCharacterRelationships_MovieCharacters_MovieCharacterId",
                        column: x => x.MovieCharacterId,
                        principalTable: "MovieCharacters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MovieCharacterRelationships_MovieCharacters_RelatedCharacte~",
                        column: x => x.RelatedCharacterId,
                        principalTable: "MovieCharacters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MovieCharacterStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieCharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Label = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    Wardrobe = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    AgeOrTimeState = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Appearance = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    InjuryOrCondition = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    LocationOrStoryState = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ContinuityNotes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieCharacterStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieCharacterStates_MovieCharacters_MovieCharacterId",
                        column: x => x.MovieCharacterId,
                        principalTable: "MovieCharacters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieCharacterContinuityLocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieCharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieCharacterStateId = table.Column<Guid>(type: "uuid", nullable: true),
                    FieldKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    LockedValue = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieCharacterContinuityLocks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieCharacterContinuityLocks_AspNetUsers_ApprovedByUserId",
                        column: x => x.ApprovedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieCharacterContinuityLocks_MovieCharacterStates_MovieCha~",
                        column: x => x.MovieCharacterStateId,
                        principalTable: "MovieCharacterStates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieCharacterContinuityLocks_MovieCharacters_MovieCharacte~",
                        column: x => x.MovieCharacterId,
                        principalTable: "MovieCharacters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MovieCharacterContinuityLocks_ApprovedByUserId",
                table: "MovieCharacterContinuityLocks",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieCharacterContinuityLocks_MovieCharacterId_MovieCharact~",
                table: "MovieCharacterContinuityLocks",
                columns: new[] { "MovieCharacterId", "MovieCharacterStateId", "FieldKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieCharacterContinuityLocks_MovieCharacterStateId",
                table: "MovieCharacterContinuityLocks",
                column: "MovieCharacterStateId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieCharacterReferenceAssets_AssetId",
                table: "MovieCharacterReferenceAssets",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieCharacterReferenceAssets_MovieCharacterId_SortOrder",
                table: "MovieCharacterReferenceAssets",
                columns: new[] { "MovieCharacterId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieCharacterRelationships_MovieCharacterId_RelatedCharact~",
                table: "MovieCharacterRelationships",
                columns: new[] { "MovieCharacterId", "RelatedCharacterId", "RelationshipType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieCharacterRelationships_RelatedCharacterId",
                table: "MovieCharacterRelationships",
                column: "RelatedCharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieCharacterStates_MovieCharacterId_Key",
                table: "MovieCharacterStates",
                columns: new[] { "MovieCharacterId", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MovieCharacterContinuityLocks");

            migrationBuilder.DropTable(
                name: "MovieCharacterReferenceAssets");

            migrationBuilder.DropTable(
                name: "MovieCharacterRelationships");

            migrationBuilder.DropTable(
                name: "MovieCharacterStates");

            migrationBuilder.DropColumn(
                name: "PersonalityAndStoryNotes",
                table: "MovieCharacters");

            migrationBuilder.DropColumn(
                name: "PhysicalDescription",
                table: "MovieCharacters");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "MovieCharacters");

            migrationBuilder.DropColumn(
                name: "VoiceReference",
                table: "MovieCharacters");

            migrationBuilder.DropColumn(
                name: "Wardrobe",
                table: "MovieCharacters");

            migrationBuilder.AlterColumn<string>(
                name: "ContinuitySnapshotJson",
                table: "MovieClips",
                type: "character varying(20000)",
                maxLength: 20000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100000)",
                oldMaxLength: 100000,
                oldNullable: true);
        }
    }
}
