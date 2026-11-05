using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeadLetters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dead_letters",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    topic = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    consumer_group = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    consumer = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: true),
                    envelope = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    exception_type = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    exception_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    failed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    resolution_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    replay_message_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dead_letters", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_dead_letters_message_id",
                schema: "platform",
                table: "dead_letters",
                column: "message_id");

            migrationBuilder.CreateIndex(
                name: "ix_dead_letters_status_consumer_group_failed_at",
                schema: "platform",
                table: "dead_letters",
                columns: new[] { "status", "consumer_group", "failed_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dead_letters",
                schema: "platform");
        }
    }
}
