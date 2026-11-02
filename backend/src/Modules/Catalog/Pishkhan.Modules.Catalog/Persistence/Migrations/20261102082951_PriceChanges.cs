using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Catalog.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PriceChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "price_changes",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "jsonb", nullable: false),
                    rule = table.Column<string>(type: "jsonb", nullable: false),
                    items = table.Column<string>(type: "jsonb", nullable: false),
                    product_count = table.Column<int>(type: "integer", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    undone_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    undone_by = table.Column<Guid>(type: "uuid", nullable: true),
                    undo_skipped = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_price_changes", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_price_changes_workspace_id_created_at",
                schema: "catalog",
                table: "price_changes",
                columns: new[] { "workspace_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "price_changes",
                schema: "catalog");
        }
    }
}
