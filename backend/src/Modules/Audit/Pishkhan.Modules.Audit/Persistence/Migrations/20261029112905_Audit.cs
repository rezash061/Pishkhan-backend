using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Audit.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Audit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "audit");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            // Append-only, range-partitioned by month on occurred_at (backend plan 5). EF cannot express PARTITION BY, so the parent
            // table is written by hand; it must stay in sync with the model (columns, types, primary key).
            // Partitions are created by the Migrator and the partition-maintenance job.
            migrationBuilder.Sql("""
                CREATE TABLE audit.audit_entries (
                    id uuid NOT NULL,
                    occurred_at timestamp with time zone NOT NULL,
                    recorded_at timestamp with time zone NOT NULL,
                    actor_type character varying(10) NOT NULL,
                    actor_id uuid NULL,
                    actor_name character varying(100) NOT NULL,
                    on_behalf_of uuid NULL,
                    workspace_id uuid NULL,
                    action character varying(80) NOT NULL,
                    entity_type character varying(60) NOT NULL,
                    entity_id character varying(100) NULL,
                    reason character varying(500) NULL,
                    ip character varying(45) NULL,
                    before jsonb NULL,
                    after jsonb NULL,
                    CONSTRAINT pk_audit_entries PRIMARY KEY (id, occurred_at)
                ) PARTITION BY RANGE (occurred_at);
                """);

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_actor_id_occurred_at",
                schema: "audit",
                table: "audit_entries",
                columns: new[] { "actor_id", "occurred_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_entity_type_entity_id",
                schema: "audit",
                table: "audit_entries",
                columns: new[] { "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_occurred_at_id",
                schema: "audit",
                table: "audit_entries",
                columns: new[] { "occurred_at", "id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_workspace_id_occurred_at",
                schema: "audit",
                table: "audit_entries",
                columns: new[] { "workspace_id", "occurred_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_entries",
                schema: "audit");
        }
    }
}
