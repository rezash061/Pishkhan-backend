using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Conversations.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CrmTagsFieldsSegments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "contact_fields",
                schema: "conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    label = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    options = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'"),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_contact_fields", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "contact_segments",
                schema: "conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    rule = table.Column<string>(type: "jsonb", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_contact_segments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "contact_tags",
                schema: "conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    color = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_contact_tags", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_contact_fields_workspace_id_key",
                schema: "conversations",
                table: "contact_fields",
                columns: new[] { "workspace_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_contact_segments_workspace_id_name",
                schema: "conversations",
                table: "contact_segments",
                columns: new[] { "workspace_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_contact_tags_workspace_id_name",
                schema: "conversations",
                table: "contact_tags",
                columns: new[] { "workspace_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contact_fields",
                schema: "conversations");

            migrationBuilder.DropTable(
                name: "contact_segments",
                schema: "conversations");

            migrationBuilder.DropTable(
                name: "contact_tags",
                schema: "conversations");
        }
    }
}
