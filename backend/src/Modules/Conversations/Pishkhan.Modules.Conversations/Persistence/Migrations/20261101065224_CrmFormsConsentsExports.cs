using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Conversations.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CrmFormsConsentsExports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "contact_consents",
                schema: "conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    granted = table.Column<bool>(type: "boolean", nullable: false),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    evidence = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    recorded_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_contact_consents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "contact_exports",
                schema: "conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    filter = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    row_count = table.Column<int>(type: "integer", nullable: true),
                    storage_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    error = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    downloaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_contact_exports", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "form_submissions",
                schema: "conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    flow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    flow_version = table.Column<int>(type: "integer", nullable: false),
                    flow_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    answers = table.Column<string>(type: "jsonb", nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_form_submissions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_contact_consents_contact_id_channel_created_at",
                schema: "conversations",
                table: "contact_consents",
                columns: new[] { "contact_id", "channel", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_contact_exports_workspace_id_created_at",
                schema: "conversations",
                table: "contact_exports",
                columns: new[] { "workspace_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_form_submissions_contact_id",
                schema: "conversations",
                table: "form_submissions",
                column: "contact_id");

            migrationBuilder.CreateIndex(
                name: "ix_form_submissions_conversation_id",
                schema: "conversations",
                table: "form_submissions",
                column: "conversation_id");

            migrationBuilder.CreateIndex(
                name: "ix_form_submissions_run_id",
                schema: "conversations",
                table: "form_submissions",
                column: "run_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_form_submissions_workspace_id_flow_id_submitted_at",
                schema: "conversations",
                table: "form_submissions",
                columns: new[] { "workspace_id", "flow_id", "submitted_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contact_consents",
                schema: "conversations");

            migrationBuilder.DropTable(
                name: "contact_exports",
                schema: "conversations");

            migrationBuilder.DropTable(
                name: "form_submissions",
                schema: "conversations");
        }
    }
}
