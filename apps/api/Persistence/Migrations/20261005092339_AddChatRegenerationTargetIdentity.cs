using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChatRegenerationTargetIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RegenerationTargetMessageId",
                table: "ChatMessages",
                type: "uuid",
                nullable: true);

            // Regeneration assistants are appended immediately after their target assistant.
            // Backfill legacy rows so an in-flight retry keeps its original idempotency binding.
            migrationBuilder.Sql("""
                UPDATE "ChatMessages" AS "regeneration"
                SET "RegenerationTargetMessageId" = "target"."Id"
                FROM "ChatMessages" AS "target"
                WHERE "regeneration"."RegenerationTargetMessageId" IS NULL
                  AND "regeneration"."Role" = 'Assistant'
                  AND "regeneration"."RequestId" IS NOT NULL
                  AND "target"."ConversationId" = "regeneration"."ConversationId"
                  AND "target"."Sequence" = "regeneration"."Sequence" - 1
                  AND "target"."Role" = 'Assistant'
                  AND "target"."Status" = 'Completed';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RegenerationTargetMessageId",
                table: "ChatMessages");
        }
    }
}
