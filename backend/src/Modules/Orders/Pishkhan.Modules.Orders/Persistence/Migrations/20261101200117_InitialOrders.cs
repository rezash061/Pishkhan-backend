using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Orders.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "orders");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "counters",
                schema: "orders",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_number = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_counters", x => x.workspace_id);
                });

            migrationBuilder.CreateTable(
                name: "order_events",
                schema: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    from_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    to_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    actor_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "orders",
                schema: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<long>(type: "bigint", nullable: false),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: true),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    customer_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    phone_enc = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    phone_bidx = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    address = table.Column<string>(type: "jsonb", nullable: true),
                    items = table.Column<string>(type: "jsonb", nullable: false),
                    subtotal = table.Column<long>(type: "bigint", nullable: false),
                    shipping_fee = table.Column<long>(type: "bigint", nullable: false),
                    discount = table.Column<long>(type: "bigint", nullable: false),
                    total = table.Column<long>(type: "bigint", nullable: false),
                    note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orders", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_order_events_workspace_id_order_id_created_at",
                schema: "orders",
                table: "order_events",
                columns: new[] { "workspace_id", "order_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_workspace_id_contact_id",
                schema: "orders",
                table: "orders",
                columns: new[] { "workspace_id", "contact_id" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_workspace_id_conversation_id",
                schema: "orders",
                table: "orders",
                columns: new[] { "workspace_id", "conversation_id" },
                filter: "conversation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_orders_workspace_id_number",
                schema: "orders",
                table: "orders",
                columns: new[] { "workspace_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_orders_workspace_id_phone_bidx",
                schema: "orders",
                table: "orders",
                columns: new[] { "workspace_id", "phone_bidx" },
                filter: "phone_bidx IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_orders_workspace_id_status_created_at",
                schema: "orders",
                table: "orders",
                columns: new[] { "workspace_id", "status", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "counters",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "order_events",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "orders",
                schema: "orders");
        }
    }
}
