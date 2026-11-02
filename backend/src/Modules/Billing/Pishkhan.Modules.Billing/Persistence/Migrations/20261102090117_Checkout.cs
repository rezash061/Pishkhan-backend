using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Billing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Checkout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "invoice_numbers",
                schema: "billing",
                startValue: 1001L);

            migrationBuilder.AddColumn<Guid>(
                name: "checkout_id",
                schema: "billing",
                table: "subscriptions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cycle",
                schema: "billing",
                table: "subscriptions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "paid_amount",
                schema: "billing",
                table: "subscriptions",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "checkouts",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    plan_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    plan_version = table.Column<int>(type: "integer", nullable: false),
                    cycle = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    price = table.Column<long>(type: "bigint", nullable: false),
                    credit = table.Column<long>(type: "bigint", nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    replaces_subscription_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    authority = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    reference_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    card_mask = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    error = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    invoice_number = table.Column<long>(type: "bigint", nullable: true),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_checkouts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_checkouts_authority",
                schema: "billing",
                table: "checkouts",
                column: "authority",
                unique: true,
                filter: "authority IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_checkouts_invoice_number",
                schema: "billing",
                table: "checkouts",
                column: "invoice_number",
                unique: true,
                filter: "invoice_number IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_checkouts_workspace_id_created_at",
                schema: "billing",
                table: "checkouts",
                columns: new[] { "workspace_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "checkouts",
                schema: "billing");

            migrationBuilder.DropColumn(
                name: "checkout_id",
                schema: "billing",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "cycle",
                schema: "billing",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "paid_amount",
                schema: "billing",
                table: "subscriptions");

            migrationBuilder.DropSequence(
                name: "invoice_numbers",
                schema: "billing");
        }
    }
}
