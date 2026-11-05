using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Payments.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Receipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bank_accounts",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    card_number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    sheba = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: true),
                    holder_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    bank_code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bank_accounts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "receipt_settings",
                schema: "payments",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    auto_approve = table.Column<bool>(type: "boolean", nullable: false),
                    threshold = table.Column<double>(type: "double precision", nullable: false),
                    amount_tolerance = table.Column<long>(type: "bigint", nullable: false),
                    window_hours = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_receipt_settings", x => x.workspace_id);
                });

            migrationBuilder.CreateTable(
                name: "receipts",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: true),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: true),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reference = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    source_card = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    destination_card = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    destination_sheba = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    bank = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    confidence = table.Column<double>(type: "double precision", nullable: true),
                    image_hash = table.Column<long>(type: "bigint", nullable: true),
                    checks = table.Column<string>(type: "jsonb", nullable: false),
                    duplicate_of_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reject_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    submitted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_receipts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_bank_accounts_workspace_id_created_at",
                schema: "payments",
                table: "bank_accounts",
                columns: new[] { "workspace_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_receipts_file_id",
                schema: "payments",
                table: "receipts",
                column: "file_id");

            migrationBuilder.CreateIndex(
                name: "ix_receipts_message_id",
                schema: "payments",
                table: "receipts",
                column: "message_id",
                unique: true,
                filter: "message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_receipts_workspace_id_order_id",
                schema: "payments",
                table: "receipts",
                columns: new[] { "workspace_id", "order_id" });

            migrationBuilder.CreateIndex(
                name: "ix_receipts_workspace_id_reference",
                schema: "payments",
                table: "receipts",
                columns: new[] { "workspace_id", "reference" },
                filter: "reference IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_receipts_workspace_id_status_created_at",
                schema: "payments",
                table: "receipts",
                columns: new[] { "workspace_id", "status", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bank_accounts",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "receipt_settings",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "receipts",
                schema: "payments");
        }
    }
}
