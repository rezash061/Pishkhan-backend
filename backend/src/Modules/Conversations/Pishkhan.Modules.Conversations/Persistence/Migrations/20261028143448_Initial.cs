using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Conversations.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "conversations");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "contacts",
                schema: "conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    username = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_contacts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "contacts_channel_identities",
                schema: "conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_contacts_channel_identities", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "conversations",
                schema: "conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    assignee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ai_paused = table.Column<bool>(type: "boolean", nullable: false),
                    last_inbound_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_outbound_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    window_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    unread_count = table.Column<int>(type: "integer", nullable: false),
                    last_message_preview = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conversations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "external_message_keys",
                schema: "conversations",
                columns: table => new
                {
                    channel_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_external_message_keys", x => new { x.channel_account_id, x.external_id });
                });

            // Range-partitioned by month on created_at (backend plan 5). EF cannot express PARTITION BY, so the parent
            // table is written by hand; it must stay in sync with the model (columns, types, primary key).
            // Partitions are created by the Migrator and the partition-maintenance job.
            migrationBuilder.Sql("""
                CREATE TABLE conversations.messages (
                    id uuid NOT NULL,
                    created_at timestamp with time zone NOT NULL,
                    updated_at timestamp with time zone NOT NULL,
                    workspace_id uuid NOT NULL,
                    conversation_id uuid NOT NULL,
                    direction character varying(10) NOT NULL,
                    kind character varying(20) NOT NULL,
                    text character varying(4000) NULL,
                    normalized_text character varying(4000) NULL,
                    media_file_id uuid NULL,
                    attachments_json jsonb NULL,
                    external_id character varying(200) NULL,
                    status character varying(20) NOT NULL,
                    error_code character varying(80) NULL,
                    handled_by character varying(10) NULL,
                    sent_by uuid NULL,
                    sent_at timestamp with time zone NULL,
                    CONSTRAINT pk_messages PRIMARY KEY (id, created_at)
                ) PARTITION BY RANGE (created_at);
                """);

            migrationBuilder.CreateIndex(
                name: "ix_contacts_workspace_id",
                schema: "conversations",
                table: "contacts",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ix_contacts_channel_identities_channel_account_id_external_use",
                schema: "conversations",
                table: "contacts_channel_identities",
                columns: new[] { "channel_account_id", "external_user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_contacts_channel_identities_contact_id",
                schema: "conversations",
                table: "contacts_channel_identities",
                column: "contact_id");

            migrationBuilder.CreateIndex(
                name: "ix_conversations_channel_account_id_contact_id",
                schema: "conversations",
                table: "conversations",
                columns: new[] { "channel_account_id", "contact_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_conversations_workspace_id_status_last_inbound_at",
                schema: "conversations",
                table: "conversations",
                columns: new[] { "workspace_id", "status", "last_inbound_at" });

            migrationBuilder.CreateIndex(
                name: "ix_messages_conversation_id_created_at_id",
                schema: "conversations",
                table: "messages",
                columns: new[] { "conversation_id", "created_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contacts",
                schema: "conversations");

            migrationBuilder.DropTable(
                name: "contacts_channel_identities",
                schema: "conversations");

            migrationBuilder.DropTable(
                name: "conversations",
                schema: "conversations");

            migrationBuilder.DropTable(
                name: "external_message_keys",
                schema: "conversations");

            migrationBuilder.DropTable(
                name: "messages",
                schema: "conversations");
        }
    }
}
