using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Files.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "files");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "files",
                schema: "files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    original_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    content_type_declared = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    content_type_detected = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    bucket = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    scan_engine_version = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    signature = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    scanned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    scan_attempts = table.Column<int>(type: "integer", nullable: false),
                    reject_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: true),
                    derived = table.Column<string>(type: "jsonb", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_files", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_files_sha256_status",
                schema: "files",
                table: "files",
                columns: new[] { "sha256", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_files_status",
                schema: "files",
                table: "files",
                column: "status",
                filter: "status IN ('pending_scan', 'scan_failed', 'infected')");

            migrationBuilder.CreateIndex(
                name: "ix_files_workspace_id_created_at",
                schema: "files",
                table: "files",
                columns: new[] { "workspace_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "files",
                schema: "files");
        }
    }
}
