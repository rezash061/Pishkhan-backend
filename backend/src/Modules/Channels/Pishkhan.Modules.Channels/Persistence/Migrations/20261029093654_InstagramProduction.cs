using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Channels.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InstagramProduction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "completed_modules",
                schema: "channels",
                table: "data_deletion_requests",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<int>(
                name: "items_deleted",
                schema: "channels",
                table: "data_deletion_requests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "last_refresh_error",
                schema: "channels",
                table: "channel_accounts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "refresh_failures",
                schema: "channels",
                table: "channel_accounts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "status_changed_at",
                schema: "channels",
                table: "channel_accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status_reason",
                schema: "channels",
                table: "channel_accounts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "token_refreshed_at",
                schema: "channels",
                table: "channel_accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "rate_profiles",
                schema: "channels",
                columns: table => new
                {
                    name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    values = table.Column<string>(type: "jsonb", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rate_profiles", x => x.name);
                });

            migrationBuilder.CreateIndex(
                name: "ix_data_deletion_requests_external_user_id",
                schema: "channels",
                table: "data_deletion_requests",
                column: "external_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_channel_accounts_token_expires_at",
                schema: "channels",
                table: "channel_accounts",
                column: "token_expires_at",
                filter: "status IN ('active', 'token_expiring')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rate_profiles",
                schema: "channels");

            migrationBuilder.DropIndex(
                name: "ix_data_deletion_requests_external_user_id",
                schema: "channels",
                table: "data_deletion_requests");

            migrationBuilder.DropIndex(
                name: "ix_channel_accounts_token_expires_at",
                schema: "channels",
                table: "channel_accounts");

            migrationBuilder.DropColumn(
                name: "completed_modules",
                schema: "channels",
                table: "data_deletion_requests");

            migrationBuilder.DropColumn(
                name: "items_deleted",
                schema: "channels",
                table: "data_deletion_requests");

            migrationBuilder.DropColumn(
                name: "last_refresh_error",
                schema: "channels",
                table: "channel_accounts");

            migrationBuilder.DropColumn(
                name: "refresh_failures",
                schema: "channels",
                table: "channel_accounts");

            migrationBuilder.DropColumn(
                name: "status_changed_at",
                schema: "channels",
                table: "channel_accounts");

            migrationBuilder.DropColumn(
                name: "status_reason",
                schema: "channels",
                table: "channel_accounts");

            migrationBuilder.DropColumn(
                name: "token_refreshed_at",
                schema: "channels",
                table: "channel_accounts");
        }
    }
}
