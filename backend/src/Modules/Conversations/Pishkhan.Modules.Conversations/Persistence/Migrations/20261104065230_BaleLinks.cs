using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Conversations.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BaleLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "contact_link_tokens",
                schema: "conversations",
                columns: table => new
                {
                    token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_contact_link_tokens", x => x.token);
                });

            migrationBuilder.CreateIndex(
                name: "ix_contact_link_tokens_workspace_id_contact_id",
                schema: "conversations",
                table: "contact_link_tokens",
                columns: new[] { "workspace_id", "contact_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contact_link_tokens",
                schema: "conversations");
        }
    }
}
