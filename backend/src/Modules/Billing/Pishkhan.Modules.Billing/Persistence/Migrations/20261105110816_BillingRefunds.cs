using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Billing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BillingRefunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "refunds",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    checkout_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    reference_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ended_subscription = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refunds", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_checkouts_status_paid_at",
                schema: "billing",
                table: "checkouts",
                columns: new[] { "status", "paid_at" });

            migrationBuilder.CreateIndex(
                name: "ix_refunds_checkout_id",
                schema: "billing",
                table: "refunds",
                column: "checkout_id");

            migrationBuilder.CreateIndex(
                name: "ix_refunds_created_at",
                schema: "billing",
                table: "refunds",
                column: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "refunds",
                schema: "billing");

            migrationBuilder.DropIndex(
                name: "ix_checkouts_status_paid_at",
                schema: "billing",
                table: "checkouts");
        }
    }
}
