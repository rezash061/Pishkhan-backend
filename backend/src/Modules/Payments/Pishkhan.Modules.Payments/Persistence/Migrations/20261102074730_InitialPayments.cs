using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Payments.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "payments");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "attempts",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    link_id = table.Column<Guid>(type: "uuid", nullable: false),
                    authority = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attempts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "links",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_number = table.Column<long>(type: "bigint", nullable: false),
                    shop_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    lines = table.Column<string>(type: "jsonb", nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reference_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    card_mask = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    last_error = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_links", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "merchant_accounts",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    merchant_id_enc = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    merchant_id_hint = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    sandbox = table.Column<bool>(type: "boolean", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_merchant_accounts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_attempts_authority",
                schema: "payments",
                table: "attempts",
                column: "authority",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_attempts_workspace_id_link_id",
                schema: "payments",
                table: "attempts",
                columns: new[] { "workspace_id", "link_id" });

            migrationBuilder.CreateIndex(
                name: "ix_links_token",
                schema: "payments",
                table: "links",
                column: "token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_links_workspace_id_order_id_created_at",
                schema: "payments",
                table: "links",
                columns: new[] { "workspace_id", "order_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_merchant_accounts_workspace_id",
                schema: "payments",
                table: "merchant_accounts",
                column: "workspace_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attempts",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "links",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "merchant_accounts",
                schema: "payments");
        }
    }
}
