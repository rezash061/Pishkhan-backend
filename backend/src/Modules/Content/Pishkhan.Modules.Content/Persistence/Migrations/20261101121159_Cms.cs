using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Content.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Cms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "categories",
                schema: "content",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    slug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_categories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "entries",
                schema: "content",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    slug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    category = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    author = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    body = table.Column<string>(type: "jsonb", nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    published = table.Column<string>(type: "jsonb", nullable: true),
                    published_revision = table.Column<int>(type: "integer", nullable: true),
                    live_category = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    search_text = table.Column<string>(type: "text", nullable: true),
                    first_published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    scheduled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "redirects",
                schema: "content",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_path = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    to_path = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    permanent = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_redirects", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_categories_kind_slug",
                schema: "content",
                table: "categories",
                columns: new[] { "kind", "slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_entries_kind_slug",
                schema: "content",
                table: "entries",
                columns: new[] { "kind", "slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_entries_kind_status_live_category_first_published_at",
                schema: "content",
                table: "entries",
                columns: new[] { "kind", "status", "live_category", "first_published_at" });

            migrationBuilder.CreateIndex(
                name: "ix_entries_scheduled_at",
                schema: "content",
                table: "entries",
                column: "scheduled_at",
                filter: "scheduled_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_redirects_from_path",
                schema: "content",
                table: "redirects",
                column: "from_path",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "categories",
                schema: "content");

            migrationBuilder.DropTable(
                name: "entries",
                schema: "content");

            migrationBuilder.DropTable(
                name: "redirects",
                schema: "content");
        }
    }
}
