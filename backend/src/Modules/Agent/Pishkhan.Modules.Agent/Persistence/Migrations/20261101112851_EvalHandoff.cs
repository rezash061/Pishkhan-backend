using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Agent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EvalHandoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "handoff_precision",
                schema: "agent",
                table: "eval_runs",
                type: "numeric(5,4)",
                precision: 5,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "handoff_recall",
                schema: "agent",
                table: "eval_runs",
                type: "numeric(5,4)",
                precision: 5,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "handoff_correct",
                schema: "agent",
                table: "eval_results",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "handoff_reason",
                schema: "agent",
                table: "eval_results",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "predicted_handoff",
                schema: "agent",
                table: "eval_results",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "handoff_precision",
                schema: "agent",
                table: "eval_runs");

            migrationBuilder.DropColumn(
                name: "handoff_recall",
                schema: "agent",
                table: "eval_runs");

            migrationBuilder.DropColumn(
                name: "handoff_correct",
                schema: "agent",
                table: "eval_results");

            migrationBuilder.DropColumn(
                name: "handoff_reason",
                schema: "agent",
                table: "eval_results");

            migrationBuilder.DropColumn(
                name: "predicted_handoff",
                schema: "agent",
                table: "eval_results");
        }
    }
}
