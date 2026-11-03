using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Agent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FinglishEval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "finglish_case_count",
                schema: "agent",
                table: "eval_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "finglish_intent_accuracy",
                schema: "agent",
                table: "eval_runs",
                type: "numeric(5,4)",
                precision: 5,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "script_intent_accuracy",
                schema: "agent",
                table: "eval_runs",
                type: "numeric(5,4)",
                precision: 5,
                scale: 4,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "finglish_case_count",
                schema: "agent",
                table: "eval_runs");

            migrationBuilder.DropColumn(
                name: "finglish_intent_accuracy",
                schema: "agent",
                table: "eval_runs");

            migrationBuilder.DropColumn(
                name: "script_intent_accuracy",
                schema: "agent",
                table: "eval_runs");
        }
    }
}
