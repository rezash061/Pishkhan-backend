using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Knowledge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FeedImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "feed_import_items",
                schema: "knowledge",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    import_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    media_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    permalink = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    image_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    price = table.Column<long>(type: "bigint", nullable: true),
                    price_in_caption = table.Column<bool>(type: "boolean", nullable: false),
                    tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    question = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    answer = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_feed_import_items", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "feed_imports",
                schema: "knowledge",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    max_posts_to_read = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    posts_read = table.Column<int>(type: "integer", nullable: false),
                    posts_analyzed = table.Column<int>(type: "integer", nullable: false),
                    items_found = table.Column<int>(type: "integer", nullable: false),
                    posts_failed = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_feed_imports", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_feed_import_items_workspace_id_import_id_status",
                schema: "knowledge",
                table: "feed_import_items",
                columns: new[] { "workspace_id", "import_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_feed_imports_workspace_id",
                schema: "knowledge",
                table: "feed_imports",
                column: "workspace_id",
                unique: true,
                filter: "status = 'running'");

            migrationBuilder.CreateIndex(
                name: "ix_feed_imports_workspace_id_created_at",
                schema: "knowledge",
                table: "feed_imports",
                columns: new[] { "workspace_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "feed_import_items",
                schema: "knowledge");

            migrationBuilder.DropTable(
                name: "feed_imports",
                schema: "knowledge");
        }
    }
}
