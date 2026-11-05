using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Files.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class QuarantineAdmin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "false_positive_at",
                schema: "files",
                table: "files",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "false_positive_by",
                schema: "files",
                table: "files",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_files_quarantine",
                schema: "files",
                table: "files",
                columns: new[] { "status", "created_at" },
                filter: "status <> 'clean'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_files_quarantine",
                schema: "files",
                table: "files");

            migrationBuilder.DropColumn(
                name: "false_positive_at",
                schema: "files",
                table: "files");

            migrationBuilder.DropColumn(
                name: "false_positive_by",
                schema: "files",
                table: "files");
        }
    }
}
