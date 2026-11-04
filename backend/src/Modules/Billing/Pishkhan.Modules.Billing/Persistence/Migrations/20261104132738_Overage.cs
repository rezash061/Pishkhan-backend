using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Billing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Overage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "billing_alerts",
                schema: "billing",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_billing_alerts", x => new { x.workspace_id, x.key });
                });

            migrationBuilder.CreateTable(
                name: "billing_settings",
                schema: "billing",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    overage_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    cap = table.Column<long>(type: "bigint", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_billing_settings", x => x.workspace_id);
                });

            migrationBuilder.CreateTable(
                name: "overage_charges",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    period_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    conversations = table.Column<int>(type: "integer", nullable: false),
                    unit_price = table.Column<long>(type: "bigint", nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    checkout_id = table.Column<Guid>(type: "uuid", nullable: true),
                    settled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_overage_charges", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_overage_charges_checkout_id",
                schema: "billing",
                table: "overage_charges",
                column: "checkout_id");

            migrationBuilder.CreateIndex(
                name: "ix_overage_charges_workspace_id_period_start",
                schema: "billing",
                table: "overage_charges",
                columns: new[] { "workspace_id", "period_start" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_alerts",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "billing_settings",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "overage_charges",
                schema: "billing");
        }
    }
}
