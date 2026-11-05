using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Agent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AiTraceAdmin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_ai_traces_created_at",
                schema: "agent",
                table: "ai_traces",
                column: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_ai_traces_created_at",
                schema: "agent",
                table: "ai_traces");
        }
    }
}
