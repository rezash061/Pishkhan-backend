using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Knowledge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UnansweredQuestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "unanswered_questions",
                schema: "knowledge",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    question = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    count = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    first_asked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_asked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    answer_source_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_unanswered_questions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_unanswered_questions_workspace_id_key",
                schema: "knowledge",
                table: "unanswered_questions",
                columns: new[] { "workspace_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_unanswered_questions_workspace_id_status_last_asked_at",
                schema: "knowledge",
                table: "unanswered_questions",
                columns: new[] { "workspace_id", "status", "last_asked_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "unanswered_questions",
                schema: "knowledge");
        }
    }
}
