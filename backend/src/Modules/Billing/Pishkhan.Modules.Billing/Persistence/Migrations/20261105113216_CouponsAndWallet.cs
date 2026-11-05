using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Billing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CouponsAndWallet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "to_wallet",
                schema: "billing",
                table: "refunds",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "wallet_returned",
                schema: "billing",
                table: "refunds",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "coupon_code",
                schema: "billing",
                table: "checkouts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "coupon_id",
                schema: "billing",
                table: "checkouts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "discount",
                schema: "billing",
                table: "checkouts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "wallet_released",
                schema: "billing",
                table: "checkouts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "wallet_used",
                schema: "billing",
                table: "checkouts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "coupon_redemptions",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    coupon_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    checkout_id = table.Column<Guid>(type: "uuid", nullable: false),
                    discount = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_coupon_redemptions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "coupons",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    value = table.Column<long>(type: "bigint", nullable: false),
                    plan_codes = table.Column<List<string>>(type: "text[]", nullable: false),
                    first_purchase_only = table.Column<bool>(type: "boolean", nullable: false),
                    max_redemptions = table.Column<int>(type: "integer", nullable: true),
                    max_per_workspace = table.Column<int>(type: "integer", nullable: false),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    valid_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    redemptions = table.Column<int>(type: "integer", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_coupons", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "wallet_entries",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    balance_after = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    checkout_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wallet_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "wallets",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    balance = table.Column<long>(type: "bigint", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wallets", x => x.id);
                    table.CheckConstraint("ck_wallets_balance", "balance >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_checkouts_coupon_id",
                schema: "billing",
                table: "checkouts",
                column: "coupon_id",
                filter: "coupon_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_coupon_redemptions_checkout_id",
                schema: "billing",
                table: "coupon_redemptions",
                column: "checkout_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_coupon_redemptions_coupon_id_workspace_id",
                schema: "billing",
                table: "coupon_redemptions",
                columns: new[] { "coupon_id", "workspace_id" });

            migrationBuilder.CreateIndex(
                name: "ix_coupons_code",
                schema: "billing",
                table: "coupons",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_coupons_created_at",
                schema: "billing",
                table: "coupons",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_wallet_entries_checkout_id",
                schema: "billing",
                table: "wallet_entries",
                column: "checkout_id",
                filter: "checkout_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_wallet_entries_workspace_id_created_at",
                schema: "billing",
                table: "wallet_entries",
                columns: new[] { "workspace_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_wallets_workspace_id",
                schema: "billing",
                table: "wallets",
                column: "workspace_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "coupon_redemptions",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "coupons",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "wallet_entries",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "wallets",
                schema: "billing");

            migrationBuilder.DropIndex(
                name: "ix_checkouts_coupon_id",
                schema: "billing",
                table: "checkouts");

            migrationBuilder.DropColumn(
                name: "to_wallet",
                schema: "billing",
                table: "refunds");

            migrationBuilder.DropColumn(
                name: "wallet_returned",
                schema: "billing",
                table: "refunds");

            migrationBuilder.DropColumn(
                name: "coupon_code",
                schema: "billing",
                table: "checkouts");

            migrationBuilder.DropColumn(
                name: "coupon_id",
                schema: "billing",
                table: "checkouts");

            migrationBuilder.DropColumn(
                name: "discount",
                schema: "billing",
                table: "checkouts");

            migrationBuilder.DropColumn(
                name: "wallet_released",
                schema: "billing",
                table: "checkouts");

            migrationBuilder.DropColumn(
                name: "wallet_used",
                schema: "billing",
                table: "checkouts");
        }
    }
}
