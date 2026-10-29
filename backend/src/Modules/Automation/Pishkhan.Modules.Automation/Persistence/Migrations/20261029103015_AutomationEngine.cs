using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Automation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AutomationEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "automation");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "backlog_runs",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    media_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    pages = table.Column<int>(type: "integer", nullable: false),
                    comments_seen = table.Column<int>(type: "integer", nullable: false),
                    comments_queued = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_backlog_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "comment_events",
                schema: "automation",
                columns: table => new
                {
                    channel_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    comment_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    media_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    from_external_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    comment_created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    private_reply_due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    public_reply_due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    public_reply_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    public_reply_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comment_events", x => new { x.channel_account_id, x.comment_id });
                });

            migrationBuilder.CreateTable(
                name: "flow_runs",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    flow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    flow_version = table.Column<int>(type: "integer", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    external_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    current_node = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    retries = table.Column<int>(type: "integer", nullable: false),
                    variables = table.Column<string>(type: "jsonb", nullable: false),
                    waiting_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_message_created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_flow_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "flow_versions",
                schema: "automation",
                columns: table => new
                {
                    flow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    graph = table.Column<string>(type: "jsonb", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_flow_versions", x => new { x.flow_id, x.version });
                });

            migrationBuilder.CreateTable(
                name: "flows",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    draft = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    published_version = table.Column<int>(type: "integer", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_flows", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pacing",
                schema: "automation",
                columns: table => new
                {
                    channel_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    next_slot_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pacing", x => new { x.channel_account_id, x.family });
                });

            migrationBuilder.CreateTable(
                name: "rule_firings",
                schema: "automation",
                columns: table => new
                {
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_fired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rule_firings", x => new { x.rule_id, x.conversation_id });
                });

            migrationBuilder.CreateTable(
                name: "rule_stats",
                schema: "automation",
                columns: table => new
                {
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fired = table.Column<int>(type: "integer", nullable: false),
                    private_replies = table.Column<int>(type: "integer", nullable: false),
                    public_replies = table.Column<int>(type: "integer", nullable: false),
                    flows_started = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rule_stats", x => new { x.rule_id, x.day });
                });

            migrationBuilder.CreateTable(
                name: "rules",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    trigger_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    trigger = table.Column<string>(type: "jsonb", nullable: false),
                    actions = table.Column<string>(type: "jsonb", nullable: false),
                    post_external_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "settings",
                schema: "automation",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ai_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    business_hours = table.Column<string>(type: "jsonb", nullable: false),
                    fallback_text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    fallback_cooldown_minutes = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_settings", x => x.workspace_id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_backlog_runs_running",
                schema: "automation",
                table: "backlog_runs",
                column: "rule_id",
                unique: true,
                filter: "status = 'running'");

            migrationBuilder.CreateIndex(
                name: "ix_comment_events_from_external_id",
                schema: "automation",
                table: "comment_events",
                column: "from_external_id");

            migrationBuilder.CreateIndex(
                name: "ix_comment_events_rule_id_received_at",
                schema: "automation",
                table: "comment_events",
                columns: new[] { "rule_id", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ix_comment_events_workspace_id_media_id",
                schema: "automation",
                table: "comment_events",
                columns: new[] { "workspace_id", "media_id" });

            migrationBuilder.CreateIndex(
                name: "ix_flow_runs_external_user_id",
                schema: "automation",
                table: "flow_runs",
                column: "external_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_flow_runs_flow_id_status",
                schema: "automation",
                table: "flow_runs",
                columns: new[] { "flow_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_flow_runs_active",
                schema: "automation",
                table: "flow_runs",
                column: "conversation_id",
                unique: true,
                filter: "status IN ('waiting_input', 'waiting_timer')");

            migrationBuilder.CreateIndex(
                name: "ix_flows_workspace_id",
                schema: "automation",
                table: "flows",
                column: "workspace_id",
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_rules_workspace_id_enabled",
                schema: "automation",
                table: "rules",
                columns: new[] { "workspace_id", "enabled" },
                filter: "deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "backlog_runs",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "comment_events",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "flow_runs",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "flow_versions",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "flows",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "pacing",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "rule_firings",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "rule_stats",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "rules",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "settings",
                schema: "automation");
        }
    }
}
