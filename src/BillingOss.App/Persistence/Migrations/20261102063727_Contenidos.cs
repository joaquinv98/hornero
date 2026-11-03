using System;
using BillingOss.App.Platform;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BillingOss.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Contenidos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "content");

            migrationBuilder.AddColumn<long>(
                name: "group_id",
                schema: "accounts",
                table: "accounts",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "announcements",
                schema: "content",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    publish_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    pinned = table.Column<bool>(type: "boolean", nullable: false),
                    audience = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    product_ids = table.Column<long[]>(type: "bigint[]", nullable: false),
                    group_ids = table.Column<long[]>(type: "bigint[]", nullable: false),
                    texts = table.Column<string>(type: "jsonb", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_announcements", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "article_texts",
                schema: "content",
                columns: table => new
                {
                    article_id = table.Column<long>(type: "bigint", nullable: false),
                    language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    body = table.Column<string>(type: "text", nullable: false),
                    tags = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_article_texts", x => new { x.article_id, x.language });
                });

            migrationBuilder.CreateTable(
                name: "article_votes",
                schema: "content",
                columns: table => new
                {
                    article_id = table.Column<long>(type: "bigint", nullable: false),
                    voter = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    helpful = table.Column<bool>(type: "boolean", nullable: false),
                    voted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_article_votes", x => new { x.article_id, x.voter });
                });

            migrationBuilder.CreateTable(
                name: "articles",
                schema: "content",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    category_id = table.Column<long>(type: "bigint", nullable: false),
                    slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    featured = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    audience = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    product_ids = table.Column<long[]>(type: "bigint[]", nullable: false),
                    group_ids = table.Column<long[]>(type: "bigint[]", nullable: false),
                    views = table.Column<long>(type: "bigint", nullable: false),
                    helpful = table.Column<long>(type: "bigint", nullable: false),
                    unhelpful = table.Column<long>(type: "bigint", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_articles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "categories",
                schema: "content",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    parent_id = table.Column<long>(type: "bigint", nullable: true),
                    slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    hidden = table.Column<bool>(type: "boolean", nullable: false),
                    texts = table.Column<string>(type: "jsonb", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_categories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "components",
                schema: "content",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    parent_id = table.Column<long>(type: "bigint", nullable: true),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    hidden = table.Column<bool>(type: "boolean", nullable: false),
                    product_ids = table.Column<long[]>(type: "bigint[]", nullable: false),
                    texts = table.Column<string>(type: "jsonb", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_components", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "download_files",
                schema: "content",
                columns: table => new
                {
                    download_id = table.Column<long>(type: "bigint", nullable: false),
                    bytes = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_download_files", x => x.download_id);
                });

            migrationBuilder.CreateTable(
                name: "downloads",
                schema: "content",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    category_id = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    audience = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    product_ids = table.Column<long[]>(type: "bigint[]", nullable: false),
                    group_ids = table.Column<long[]>(type: "bigint[]", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    download_count = table.Column<long>(type: "bigint", nullable: false),
                    file_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    size = table.Column<long>(type: "bigint", nullable: true),
                    sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    texts = table.Column<string>(type: "jsonb", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_downloads", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "groups",
                schema: "accounts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_groups", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "status_event_components",
                schema: "content",
                columns: table => new
                {
                    event_id = table.Column<long>(type: "bigint", nullable: false),
                    component_id = table.Column<long>(type: "bigint", nullable: false),
                    impact = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_status_event_components", x => new { x.event_id, x.component_id });
                });

            migrationBuilder.CreateTable(
                name: "status_event_updates",
                schema: "content",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    event_id = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    texts = table.Column<string>(type: "jsonb", nullable: false),
                    author = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_status_event_updates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "status_events",
                schema: "content",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    texts = table.Column<string>(type: "jsonb", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_status_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "subscribers",
                schema: "content",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    component_ids = table.Column<long[]>(type: "bigint[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscribers", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_announcements_status_publish_at",
                schema: "content",
                table: "announcements",
                columns: new[] { "status", "publish_at" });

            migrationBuilder.CreateIndex(
                name: "ix_articles_slug",
                schema: "content",
                table: "articles",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_articles_status_category_id",
                schema: "content",
                table: "articles",
                columns: new[] { "status", "category_id" });

            migrationBuilder.CreateIndex(
                name: "ix_categories_kind_parent_id",
                schema: "content",
                table: "categories",
                columns: new[] { "kind", "parent_id" });

            migrationBuilder.CreateIndex(
                name: "ix_categories_kind_slug",
                schema: "content",
                table: "categories",
                columns: new[] { "kind", "slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_components_parent_id",
                schema: "content",
                table: "components",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_components_slug",
                schema: "content",
                table: "components",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_downloads_category_id",
                schema: "content",
                table: "downloads",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_groups_name",
                schema: "accounts",
                table: "groups",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_status_event_components_component_id",
                schema: "content",
                table: "status_event_components",
                column: "component_id");

            migrationBuilder.CreateIndex(
                name: "ix_status_event_updates_event_id_posted_at",
                schema: "content",
                table: "status_event_updates",
                columns: new[] { "event_id", "posted_at" });

            migrationBuilder.CreateIndex(
                name: "ix_status_events_kind_state",
                schema: "content",
                table: "status_events",
                columns: new[] { "kind", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_status_events_starts_at",
                schema: "content",
                table: "status_events",
                column: "starts_at");

            migrationBuilder.CreateIndex(
                name: "ix_subscribers_token",
                schema: "content",
                table: "subscribers",
                column: "token",
                unique: true);

            migrationBuilder.Sql(ContentSql.Up);

            // La clave foránea de accounts.accounts.group_id endurece una tabla que ya existía (sección 7.4 de D-20), aunque
            // la versión anterior no escribe la columna.
            migrationBuilder.RequireCoreVersion("0.1.0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ContentSql.Down);

            migrationBuilder.DropTable(
                name: "announcements",
                schema: "content");

            migrationBuilder.DropTable(
                name: "article_texts",
                schema: "content");

            migrationBuilder.DropTable(
                name: "article_votes",
                schema: "content");

            migrationBuilder.DropTable(
                name: "articles",
                schema: "content");

            migrationBuilder.DropTable(
                name: "categories",
                schema: "content");

            migrationBuilder.DropTable(
                name: "components",
                schema: "content");

            migrationBuilder.DropTable(
                name: "download_files",
                schema: "content");

            migrationBuilder.DropTable(
                name: "downloads",
                schema: "content");

            migrationBuilder.DropTable(
                name: "groups",
                schema: "accounts");

            migrationBuilder.DropTable(
                name: "status_event_components",
                schema: "content");

            migrationBuilder.DropTable(
                name: "status_event_updates",
                schema: "content");

            migrationBuilder.DropTable(
                name: "status_events",
                schema: "content");

            migrationBuilder.DropTable(
                name: "subscribers",
                schema: "content");

            migrationBuilder.DropColumn(
                name: "group_id",
                schema: "accounts",
                table: "accounts");
        }
    }
}
