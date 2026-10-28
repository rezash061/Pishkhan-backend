using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Backoffice.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "backoffice");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "staff_users",
                schema: "backoffice",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    totp_secret_enc = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    totp_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    last_totp_step = table.Column<long>(type: "bigint", nullable: true),
                    roles = table.Column<List<string>>(type: "text[]", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    failed_logins = table.Column<int>(type: "integer", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staff_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "staff_sessions",
                schema: "backoffice",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    staff_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    refresh_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    idle_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    absolute_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staff_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_staff_sessions_staff_users_staff_id",
                        column: x => x.staff_id,
                        principalSchema: "backoffice",
                        principalTable: "staff_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_staff_sessions_family_id",
                schema: "backoffice",
                table: "staff_sessions",
                column: "family_id");

            migrationBuilder.CreateIndex(
                name: "ix_staff_sessions_refresh_hash",
                schema: "backoffice",
                table: "staff_sessions",
                column: "refresh_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_staff_sessions_staff_id",
                schema: "backoffice",
                table: "staff_sessions",
                column: "staff_id");

            migrationBuilder.CreateIndex(
                name: "ix_staff_users_username",
                schema: "backoffice",
                table: "staff_users",
                column: "username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "staff_sessions",
                schema: "backoffice");

            migrationBuilder.DropTable(
                name: "staff_users",
                schema: "backoffice");
        }
    }
}
