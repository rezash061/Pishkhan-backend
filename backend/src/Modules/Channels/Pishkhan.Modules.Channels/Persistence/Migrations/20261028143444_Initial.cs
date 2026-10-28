using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Channels.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "channels");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "channel_accounts",
                schema: "channels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    external_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    username = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    token_enc = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    token_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    scopes = table.Column<List<string>>(type: "text[]", nullable: false),
                    rate_profile = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    connected_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_channel_accounts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "data_deletion_requests",
                schema: "channels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    confirmation_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    external_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_deletion_requests", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_channel_accounts_kind_external_id",
                schema: "channels",
                table: "channel_accounts",
                columns: new[] { "kind", "external_id" },
                unique: true,
                filter: "status <> 'disconnected'");

            migrationBuilder.CreateIndex(
                name: "ix_channel_accounts_workspace_id_kind",
                schema: "channels",
                table: "channel_accounts",
                columns: new[] { "workspace_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_data_deletion_requests_confirmation_code",
                schema: "channels",
                table: "data_deletion_requests",
                column: "confirmation_code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "channel_accounts",
                schema: "channels");

            migrationBuilder.DropTable(
                name: "data_deletion_requests",
                schema: "channels");
        }
    }
}
