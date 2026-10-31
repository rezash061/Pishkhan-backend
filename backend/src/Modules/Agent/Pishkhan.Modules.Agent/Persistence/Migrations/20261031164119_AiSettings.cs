using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Agent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AiSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_providers",
                schema: "agent",
                columns: table => new
                {
                    name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    base_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    api_key_enc = table.Column<string>(type: "text", nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    last_test_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    last_tested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_providers", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "ai_routes",
                schema: "agent",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    temperature = table.Column<float>(type: "real", nullable: true),
                    max_output_tokens = table.Column<int>(type: "integer", nullable: true),
                    timeout_seconds = table.Column<int>(type: "integer", nullable: false),
                    input_price_per_million = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    output_price_per_million = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_routes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "prompt_activations",
                schema: "agent",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    eval_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    activated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_prompt_activations", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "prompt_versions",
                schema: "agent",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    text = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    base_version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_prompt_versions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_routes_task_position",
                schema: "agent",
                table: "ai_routes",
                columns: new[] { "task", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_prompt_versions_key_version",
                schema: "agent",
                table: "prompt_versions",
                columns: new[] { "key", "version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_providers",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "ai_routes",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "prompt_activations",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "prompt_versions",
                schema: "agent");
        }
    }
}
