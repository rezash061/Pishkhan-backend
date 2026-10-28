using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Agent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "agent");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "eval_sets",
                schema: "agent",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_eval_sets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "eval_cases",
                schema: "agent",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_ref = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    vertical = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    input = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    context_json = table.Column<string>(type: "jsonb", nullable: true),
                    expected_intent = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    expected_handoff = table.Column<bool>(type: "boolean", nullable: true),
                    expected_facts = table.Column<List<string>>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_eval_cases", x => x.id);
                    table.ForeignKey(
                        name: "fk_eval_cases_eval_sets_set_id",
                        column: x => x.set_id,
                        principalSchema: "agent",
                        principalTable: "eval_sets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "eval_runs",
                schema: "agent",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    prompt_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    prompt_version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    route = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    case_count = table.Column<int>(type: "integer", nullable: false),
                    error_count = table.Column<int>(type: "integer", nullable: false),
                    intent_accuracy = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_eval_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_eval_runs_eval_sets_set_id",
                        column: x => x.set_id,
                        principalSchema: "agent",
                        principalTable: "eval_sets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "eval_results",
                schema: "agent",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    output = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    predicted_intent = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    intent_correct = table.Column<bool>(type: "boolean", nullable: true),
                    route = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    latency_ms = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_eval_results", x => x.id);
                    table.ForeignKey(
                        name: "fk_eval_results_eval_cases_case_id",
                        column: x => x.case_id,
                        principalSchema: "agent",
                        principalTable: "eval_cases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_eval_results_eval_runs_run_id",
                        column: x => x.run_id,
                        principalSchema: "agent",
                        principalTable: "eval_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_eval_cases_set_id_external_ref",
                schema: "agent",
                table: "eval_cases",
                columns: new[] { "set_id", "external_ref" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_eval_results_case_id",
                schema: "agent",
                table: "eval_results",
                column: "case_id");

            migrationBuilder.CreateIndex(
                name: "ix_eval_results_run_id",
                schema: "agent",
                table: "eval_results",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_eval_runs_set_id_started_at",
                schema: "agent",
                table: "eval_runs",
                columns: new[] { "set_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_eval_sets_name",
                schema: "agent",
                table: "eval_sets",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "eval_results",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "eval_cases",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "eval_runs",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "eval_sets",
                schema: "agent");
        }
    }
}
