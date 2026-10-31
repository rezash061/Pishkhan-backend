using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Analytics.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Analytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "analytics");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "hourly_metrics",
                schema: "analytics",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hour = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    messages_received = table.Column<int>(type: "integer", nullable: false),
                    new_conversations = table.Column<int>(type: "integer", nullable: false),
                    replies_by_rule = table.Column<int>(type: "integer", nullable: false),
                    replies_by_ai = table.Column<int>(type: "integer", nullable: false),
                    replies_by_human = table.Column<int>(type: "integer", nullable: false),
                    handoffs = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_hourly_metrics", x => new { x.workspace_id, x.hour });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "hourly_metrics",
                schema: "analytics");
        }
    }
}
