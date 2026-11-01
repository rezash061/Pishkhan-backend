using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pishkhan.Modules.Knowledge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "knowledge");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "chunks",
                schema: "knowledge",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    normalized_text = table.Column<string>(type: "text", nullable: false),
                    token_count = table.Column<int>(type: "integer", nullable: false),
                    embedding_model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chunks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sources",
                schema: "knowledge",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    question = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    body = table.Column<string>(type: "character varying(50000)", maxLength: 50000, nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    indexed_revision = table.Column<int>(type: "integer", nullable: true),
                    chunk_count = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    indexed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sources", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_chunks_source_id_ordinal",
                schema: "knowledge",
                table: "chunks",
                columns: new[] { "source_id", "ordinal" });

            migrationBuilder.CreateIndex(
                name: "ix_chunks_workspace_id",
                schema: "knowledge",
                table: "chunks",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ix_sources_workspace_id_kind_created_at",
                schema: "knowledge",
                table: "sources",
                columns: new[] { "workspace_id", "kind", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_sources_workspace_id_product_id",
                schema: "knowledge",
                table: "sources",
                columns: new[] { "workspace_id", "product_id" },
                unique: true,
                filter: "product_id IS NOT NULL");

            // Not in the EF model: the embedding (pgvector) and the full-text vector are written and read with SQL.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS vector;");
            migrationBuilder.Sql("ALTER TABLE knowledge.chunks ADD COLUMN embedding vector(1024);");
            migrationBuilder.Sql("ALTER TABLE knowledge.chunks ADD COLUMN search tsvector GENERATED ALWAYS AS (to_tsvector('simple', normalized_text)) STORED;");
            migrationBuilder.Sql("CREATE INDEX ix_chunks_embedding ON knowledge.chunks USING hnsw (embedding vector_cosine_ops);");
            migrationBuilder.Sql("CREATE INDEX ix_chunks_search ON knowledge.chunks USING gin (search);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chunks",
                schema: "knowledge");

            migrationBuilder.DropTable(
                name: "sources",
                schema: "knowledge");
        }
    }
}
