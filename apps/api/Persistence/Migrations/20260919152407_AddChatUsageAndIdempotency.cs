using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChatUsageAndIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CachedInputTokens",
                table: "ChatMessages",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestId",
                table: "ChatMessages",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_ConversationId_RequestId",
                table: "ChatMessages",
                columns: new[] { "ConversationId", "RequestId" },
                unique: true,
                filter: "\"RequestId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChatMessages_ConversationId_RequestId",
                table: "ChatMessages");

            migrationBuilder.DropColumn(
                name: "CachedInputTokens",
                table: "ChatMessages");

            migrationBuilder.DropColumn(
                name: "RequestId",
                table: "ChatMessages");
        }
    }
}
