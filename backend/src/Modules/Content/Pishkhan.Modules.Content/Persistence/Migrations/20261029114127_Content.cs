using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Content.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Content : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "content");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "leads",
                schema: "content",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    phone_enc = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    phone_bidx = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    instagram_handle = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    vertical = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    monthly_dms = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    message = table.Column<string>(type: "character varying(2100)", maxLength: 2100, nullable: true),
                    utm = table.Column<string>(type: "jsonb", nullable: false),
                    ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    submissions = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_leads", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_leads_created_at_id",
                schema: "content",
                table: "leads",
                columns: new[] { "created_at", "id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_leads_phone_bidx_type_created_at",
                schema: "content",
                table: "leads",
                columns: new[] { "phone_bidx", "type", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "leads",
                schema: "content");
        }
    }
}
