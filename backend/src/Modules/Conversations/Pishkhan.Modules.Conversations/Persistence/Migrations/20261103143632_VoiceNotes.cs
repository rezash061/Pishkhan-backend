using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Conversations.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VoiceNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "transcript",
                schema: "conversations",
                table: "messages",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "transcript_status",
                schema: "conversations",
                table: "messages",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_messages_workspace_id_media_file_id",
                schema: "conversations",
                table: "messages",
                columns: new[] { "workspace_id", "media_file_id" },
                filter: "media_file_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_messages_workspace_id_media_file_id",
                schema: "conversations",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "transcript",
                schema: "conversations",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "transcript_status",
                schema: "conversations",
                table: "messages");
        }
    }
}
