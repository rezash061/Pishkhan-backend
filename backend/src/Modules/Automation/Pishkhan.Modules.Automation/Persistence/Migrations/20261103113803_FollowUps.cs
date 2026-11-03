using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Automation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FollowUps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "followups",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    external_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    source = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    flow_id = table.Column<Guid>(type: "uuid", nullable: true),
                    message = table.Column<string>(type: "jsonb", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cancel_on_reply = table.Column<bool>(type: "boolean", nullable: false),
                    cancel_on_purchase = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_followups", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_followups_workspace_id_conversation_id_status",
                schema: "automation",
                table: "followups",
                columns: new[] { "workspace_id", "conversation_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_followups_workspace_id_due_at",
                schema: "automation",
                table: "followups",
                columns: new[] { "workspace_id", "due_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "followups",
                schema: "automation");
        }
    }
}
