using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Templates.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "templates");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "applications",
                schema: "templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    industry = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    template_version = table.Column<int>(type: "integer", nullable: false),
                    applied_by = table.Column<Guid>(type: "uuid", nullable: true),
                    faqs_added = table.Column<int>(type: "integer", nullable: false),
                    flows_added = table.Column<int>(type: "integer", nullable: false),
                    rules_added = table.Column<int>(type: "integer", nullable: false),
                    profile_applied = table.Column<bool>(type: "boolean", nullable: false),
                    skipped = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_applications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "industry_templates",
                schema: "templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    industry = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    content = table.Column<string>(type: "jsonb", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_industry_templates", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_applications_workspace_id_created_at",
                schema: "templates",
                table: "applications",
                columns: new[] { "workspace_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_industry_templates_industry",
                schema: "templates",
                table: "industry_templates",
                column: "industry",
                unique: true,
                filter: "status = 'draft'");

            migrationBuilder.CreateIndex(
                name: "ix_industry_templates_industry_version_number",
                schema: "templates",
                table: "industry_templates",
                columns: new[] { "industry", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_industry_templates_industry1",
                schema: "templates",
                table: "industry_templates",
                column: "industry",
                unique: true,
                filter: "status = 'published'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "applications",
                schema: "templates");

            migrationBuilder.DropTable(
                name: "industry_templates",
                schema: "templates");
        }
    }
}
