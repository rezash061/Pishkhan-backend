using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SmsLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sms_log",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    purpose = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: true),
                    phone_masked = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    phone_bidx = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    error = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    segments = table.Column<int>(type: "integer", nullable: false),
                    template = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sms_log", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sms_log_created_at",
                schema: "platform",
                table: "sms_log",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_sms_log_phone_bidx_created_at",
                schema: "platform",
                table: "sms_log",
                columns: new[] { "phone_bidx", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_sms_log_workspace_id_created_at",
                schema: "platform",
                table: "sms_log",
                columns: new[] { "workspace_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sms_log",
                schema: "platform");
        }
    }
}
