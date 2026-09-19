using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeterministicChatMessageOrdering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "NextMessageSequence",
                table: "Conversations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "Sequence",
                table: "ChatMessages",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Existing Batch 3.1/3.2 rows did not have an explicit order key.
            // CreatedAt is retained as the primary historical signal; for equal
            // timestamps, user rows are placed before assistant rows and Id is
            // used only as a deterministic final tie-breaker.
            migrationBuilder.Sql("""
                WITH ordered AS (
                    SELECT "Id", ROW_NUMBER() OVER (
                        PARTITION BY "ConversationId"
                        ORDER BY "CreatedAt", CASE WHEN "Role" = 'User' THEN 0 ELSE 1 END, "Id"
                    ) AS "Sequence"
                    FROM "ChatMessages"
                )
                UPDATE "ChatMessages" AS messages
                SET "Sequence" = ordered."Sequence"
                FROM ordered
                WHERE messages."Id" = ordered."Id";

                UPDATE "Conversations" AS conversations
                SET "NextMessageSequence" = COALESCE((
                    SELECT MAX(messages."Sequence")
                    FROM "ChatMessages" AS messages
                    WHERE messages."ConversationId" = conversations."Id"
                ), 0);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_ConversationId_Sequence",
                table: "ChatMessages",
                columns: new[] { "ConversationId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChatMessages_ConversationId_Sequence",
                table: "ChatMessages");

            migrationBuilder.DropColumn(
                name: "NextMessageSequence",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "Sequence",
                table: "ChatMessages");
        }
    }
}
