using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Knowledge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KnowledgeFilesAndUrls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "file_id",
                schema: "knowledge",
                table: "sources",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "url",
                schema: "knowledge",
                table: "sources",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "file_id",
                schema: "knowledge",
                table: "sources");

            migrationBuilder.DropColumn(
                name: "url",
                schema: "knowledge",
                table: "sources");
        }
    }
}
