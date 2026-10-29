using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Conversations.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConversationHandoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "handed_off_at",
                schema: "conversations",
                table: "conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "handoff_reason",
                schema: "conversations",
                table: "conversations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "handoff_suggested_reply",
                schema: "conversations",
                table: "conversations",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "handed_off_at",
                schema: "conversations",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "handoff_reason",
                schema: "conversations",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "handoff_suggested_reply",
                schema: "conversations",
                table: "conversations");
        }
    }
}
