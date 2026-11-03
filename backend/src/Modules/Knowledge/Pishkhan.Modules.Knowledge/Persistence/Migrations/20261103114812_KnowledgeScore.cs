using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Knowledge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KnowledgeScore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "fact_key",
                schema: "knowledge",
                table: "sources",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_sources_workspace_id_fact_key",
                schema: "knowledge",
                table: "sources",
                columns: new[] { "workspace_id", "fact_key" },
                filter: "fact_key IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_sources_workspace_id_fact_key",
                schema: "knowledge",
                table: "sources");

            migrationBuilder.DropColumn(
                name: "fact_key",
                schema: "knowledge",
                table: "sources");
        }
    }
}
