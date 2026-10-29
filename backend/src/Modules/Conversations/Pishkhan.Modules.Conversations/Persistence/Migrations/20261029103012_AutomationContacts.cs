using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Conversations.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AutomationContacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "automation_rule_id",
                schema: "conversations",
                table: "messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fields",
                schema: "conversations",
                table: "contacts",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.AddColumn<List<string>>(
                name: "tags",
                schema: "conversations",
                table: "contacts",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.CreateIndex(
                name: "ix_contacts_tags",
                schema: "conversations",
                table: "contacts",
                column: "tags")
                .Annotation("Npgsql:IndexMethod", "gin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_contacts_tags",
                schema: "conversations",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "automation_rule_id",
                schema: "conversations",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "fields",
                schema: "conversations",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "tags",
                schema: "conversations",
                table: "contacts");
        }
    }
}
