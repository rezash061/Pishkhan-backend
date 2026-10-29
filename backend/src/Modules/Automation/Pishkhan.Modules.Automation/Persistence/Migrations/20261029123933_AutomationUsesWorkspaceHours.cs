using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Automation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AutomationUsesWorkspaceHours : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "business_hours",
                schema: "automation",
                table: "settings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "business_hours",
                schema: "automation",
                table: "settings",
                type: "jsonb",
                nullable: false,
                defaultValue: "");
        }
    }
}
