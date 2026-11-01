using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Agent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentReplies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_profiles",
                schema: "agent",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    tone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    brand_voice = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    reply_in_customer_language = table.Column<bool>(type: "boolean", nullable: false),
                    never_say = table.Column<string>(type: "jsonb", nullable: false),
                    confidence_threshold = table.Column<double>(type: "double precision", nullable: false),
                    handoff = table.Column<string>(type: "jsonb", nullable: false),
                    handoff_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    allowed_domains = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_profiles", x => x.workspace_id);
                });

            migrationBuilder.CreateTable(
                name: "ai_traces",
                schema: "agent",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    flow_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    goal = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    input = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    intent = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    language = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    sentiment = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    classifier_confidence = table.Column<double>(type: "double precision", nullable: true),
                    facts = table.Column<string>(type: "jsonb", nullable: false),
                    tool_calls = table.Column<string>(type: "jsonb", nullable: false),
                    guard_issues = table.Column<string>(type: "jsonb", nullable: false),
                    reply = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    self_confidence = table.Column<double>(type: "double precision", nullable: true),
                    confidence = table.Column<double>(type: "double precision", nullable: true),
                    threshold = table.Column<double>(type: "double precision", nullable: false),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    handoff_reason = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    prompts = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    models = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cost_irr = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    latency_ms = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_traces", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_traces_conversation_id_created_at",
                schema: "agent",
                table: "ai_traces",
                columns: new[] { "conversation_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_traces_workspace_id_created_at",
                schema: "agent",
                table: "ai_traces",
                columns: new[] { "workspace_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_profiles",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "ai_traces",
                schema: "agent");
        }
    }
}
