using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Backoffice.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FeatureFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "feature_flags",
                schema: "backoffice",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    rollout_percent = table.Column<int>(type: "integer", nullable: false),
                    plans = table.Column<List<string>>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_feature_flags", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "feature_flag_overrides",
                schema: "backoffice",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_feature_flag_overrides", x => new { x.key, x.workspace_id });
                    table.ForeignKey(
                        name: "fk_feature_flag_overrides_feature_flags_key",
                        column: x => x.key,
                        principalSchema: "backoffice",
                        principalTable: "feature_flags",
                        principalColumn: "key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_feature_flag_overrides_workspace_id",
                schema: "backoffice",
                table: "feature_flag_overrides",
                column: "workspace_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "feature_flag_overrides",
                schema: "backoffice");

            migrationBuilder.DropTable(
                name: "feature_flags",
                schema: "backoffice");
        }
    }
}
