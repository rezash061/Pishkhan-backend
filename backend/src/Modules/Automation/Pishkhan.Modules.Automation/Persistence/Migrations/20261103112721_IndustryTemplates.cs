using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Automation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IndustryTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "template_key",
                schema: "automation",
                table: "rules",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "template_key",
                schema: "automation",
                table: "flows",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_rules_workspace_id_template_key",
                schema: "automation",
                table: "rules",
                columns: new[] { "workspace_id", "template_key" },
                unique: true,
                filter: "template_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_flows_workspace_id_template_key",
                schema: "automation",
                table: "flows",
                columns: new[] { "workspace_id", "template_key" },
                unique: true,
                filter: "template_key IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_rules_workspace_id_template_key",
                schema: "automation",
                table: "rules");

            migrationBuilder.DropIndex(
                name: "ix_flows_workspace_id_template_key",
                schema: "automation",
                table: "flows");

            migrationBuilder.DropColumn(
                name: "template_key",
                schema: "automation",
                table: "rules");

            migrationBuilder.DropColumn(
                name: "template_key",
                schema: "automation",
                table: "flows");
        }
    }
}
