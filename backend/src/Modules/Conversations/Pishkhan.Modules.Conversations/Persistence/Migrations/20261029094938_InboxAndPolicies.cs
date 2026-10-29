using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Conversations.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InboxAndPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "content_json",
                schema: "conversations",
                table: "messages",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "read_at",
                schema: "conversations",
                table: "messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reply_to_comment_id",
                schema: "conversations",
                table: "messages",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tag",
                schema: "conversations",
                table: "messages",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "conversation_notes",
                schema: "conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conversation_notes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "private_reply_keys",
                schema: "conversations",
                columns: table => new
                {
                    channel_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    comment_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_private_reply_keys", x => new { x.channel_account_id, x.comment_id });
                });

            migrationBuilder.CreateTable(
                name: "quick_replies",
                schema: "conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shortcut = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quick_replies", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_conversations_unread",
                schema: "conversations",
                table: "conversations",
                columns: new[] { "workspace_id", "last_inbound_at" },
                filter: "unread_count > 0");

            migrationBuilder.CreateIndex(
                name: "ix_conversations_workspace_id_assignee_id_last_inbound_at",
                schema: "conversations",
                table: "conversations",
                columns: new[] { "workspace_id", "assignee_id", "last_inbound_at" });

            migrationBuilder.CreateIndex(
                name: "ix_conversations_workspace_id_last_inbound_at_id",
                schema: "conversations",
                table: "conversations",
                columns: new[] { "workspace_id", "last_inbound_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_conversation_notes_conversation_id_created_at",
                schema: "conversations",
                table: "conversation_notes",
                columns: new[] { "conversation_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_quick_replies_workspace_id_shortcut",
                schema: "conversations",
                table: "quick_replies",
                columns: new[] { "workspace_id", "shortcut" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conversation_notes",
                schema: "conversations");

            migrationBuilder.DropTable(
                name: "private_reply_keys",
                schema: "conversations");

            migrationBuilder.DropTable(
                name: "quick_replies",
                schema: "conversations");

            migrationBuilder.DropIndex(
                name: "ix_conversations_unread",
                schema: "conversations",
                table: "conversations");

            migrationBuilder.DropIndex(
                name: "ix_conversations_workspace_id_assignee_id_last_inbound_at",
                schema: "conversations",
                table: "conversations");

            migrationBuilder.DropIndex(
                name: "ix_conversations_workspace_id_last_inbound_at_id",
                schema: "conversations",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "content_json",
                schema: "conversations",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "read_at",
                schema: "conversations",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "reply_to_comment_id",
                schema: "conversations",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "tag",
                schema: "conversations",
                table: "messages");
        }
    }
}
